using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanava.Services;
using LibVLCSharp.Shared;

namespace Kanava.ViewModels;

/// <summary>Ääni-/tekstitysraita valikkoa varten.</summary>
public sealed record TrackOption(int Id, string Name, string? Language);

/// <summary>
/// Soittimen hallinta: äänenvoimakkuus, mykistys, ääniraidan ja tekstityksen valinta sekä
/// lähetyksen tekniset tiedot (resoluutio, fps, koodekki, bittinopeus). Toimii minkä tahansa
/// MediaPlayerin kanssa (live-esikatselu ja VOD-soitin).
/// </summary>
public partial class PlaybackViewModel : ObservableObject, IDisposable
{
    private readonly MediaPlayer _player;
    private readonly LibVLC _libvlc;
    private readonly SettingsStore _settings;
    private readonly DispatcherTimer _stats = new() { Interval = TimeSpan.FromSeconds(1) };
    private double _bitrateAvg;
    private long _lastReadBytes;
    private bool _tracksDirty = true;
    private bool _autoPicked;

    [ObservableProperty] private int _volume;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private IReadOnlyList<TrackOption> _audioTracks = [];
    [ObservableProperty] private IReadOnlyList<TrackOption> _subtitleTracks = [];
    [ObservableProperty] private int _selectedAudioId = -1;
    [ObservableProperty] private int _selectedSubtitleId = -1;

    /// <summary>Esim. "1080p" – näytetään merkkinä.</summary>
    [ObservableProperty] private string _qualityBadge = "";
    /// <summary>Esim. "1920×1080 · 50 fps · H.264 · AAC".</summary>
    [ObservableProperty] private string _formatText = "";
    /// <summary>Esim. "6,4 Mbit/s".</summary>
    [ObservableProperty] private string _bitrateText = "";
    /// <summary>Esim. "Ladattu 412 Mt".</summary>
    [ObservableProperty] private string _dataText = "";
    [ObservableProperty] private bool _hasStats;

    public PlaybackViewModel(MediaPlayer player, LibVLC libvlc, SettingsStore settings)
    {
        _player = player;
        _libvlc = libvlc;
        _settings = settings;
        _volume = settings.Settings.Volume;

        _player.Playing += OnPlaying;
        _player.ESAdded += OnTracksChanged;
        _player.ESDeleted += OnTracksChanged;
        _player.ESSelected += OnTracksChanged;
        _player.Stopped += OnStopped;
        _stats.Tick += (_, _) => Poll();
        _stats.Start();
    }

    private void OnPlaying(object? sender, EventArgs e) => Ui(() =>
    {
        // VLC voi palauttaa äänenvoimakkuuden, viiveen ja kuvasuhteen uuden median alkaessa
        ApplyVolume();
        ApplyAudioDelay();
        ApplyAspect();
        _tracksDirty = true;
    });

    // ---------- Äänen synkronointi ----------

    /// <summary>Äänen viive (ms). Positiivinen = ääni myöhemmin (kun ääni on kuvaa edellä).</summary>
    [ObservableProperty] private int _audioDelayMs;
    private string? _syncKey;

    /// <summary>Asetetaan, kun toistettava kanava/elokuva vaihtuu: ladataan sille tallennettu viive.</summary>
    public void SetSyncKey(string? key)
    {
        _syncKey = key;
        var v = key is not null && _settings.Settings.AudioDelays.TryGetValue(key, out var d) ? d : 0;
        if (v != AudioDelayMs) AudioDelayMs = v; else ApplyAudioDelay();
    }

    public void ShiftAudio(int ms)
    {
        AudioDelayMs = Math.Clamp(AudioDelayMs + ms, -10000, 10000);
        ShowOsd("Äänen viive " + DelayText(AudioDelayMs) + (AudioDelayMs == 0 ? "" : AudioDelayMs > 0 ? " (ääni myöhemmin)" : " (ääni aiemmin)"));
    }

    [RelayCommand] private void AudioLater() => ShiftAudio(100);
    [RelayCommand] private void AudioEarlier() => ShiftAudio(-100);
    [RelayCommand] private void ResetAudioDelay() { AudioDelayMs = 0; ShowOsd("Äänen viive nollattu"); }

    partial void OnAudioDelayMsChanged(int value)
    {
        ApplyAudioDelay();
        if (_syncKey is null) return;
        if (value == 0) _settings.Settings.AudioDelays.Remove(_syncKey);
        else _settings.Settings.AudioDelays[_syncKey] = value;
        SaveSettings();
    }

    private void ApplyAudioDelay()
    {
        var us = AudioDelayMs * 1000L;
        Task.Run(() => { try { _player.SetAudioDelay(us); } catch { } });
    }

    public static string DelayText(int ms) => ms == 0 ? "0,0 s" : (ms > 0 ? "+" : "−") + (Math.Abs(ms) / 1000.0).ToString("0.0", CultureInfo.GetCultureInfo("fi-FI")) + " s";

    // ---------- Kuvasuhde ----------

    /// <summary>"" = automaattinen, muuten esim. "16:9". Crop = rajaus (zoom) samaan muotoon.</summary>
    [ObservableProperty] private string _aspect = "";
    [ObservableProperty] private string _crop = "";

    partial void OnAspectChanged(string value) => ApplyAspect();
    partial void OnCropChanged(string value) => ApplyAspect();

    private void ApplyAspect()
    {
        var a = Aspect.Length > 0 ? Aspect : null;
        var c = Crop.Length > 0 ? Crop : null;
        Task.Run(() => { try { _player.AspectRatio = a; _player.CropGeometry = c; } catch { } });
    }

    public void SetAspect(string aspect, string crop)
    {
        Aspect = aspect;
        Crop = crop;
        ShowOsd(crop.Length > 0 ? $"Zoom: rajaus {crop}" : aspect.Length > 0 ? $"Kuvasuhde {aspect}" : "Kuvasuhde automaattinen");
    }

    // ---------- Äänen tasoitus ----------

    /// <summary>Owner käynnistää lähetyksen uudelleen, kun tasoitus vaihdetaan (vaatii median uudelleenavauksen).</summary>
    public Action? RestartRequested { get; set; }

    public bool NormalizeAudio => _settings.Settings.NormalizeAudio;

    public void ToggleNormalize()
    {
        var on = !_settings.Settings.NormalizeAudio;
        _settings.Settings.NormalizeAudio = on;
        PlayerService.NormalizeAudio = on;
        SaveSettings();
        OnPropertyChanged(nameof(NormalizeAudio));
        ShowOsd(on ? "Äänen tasoitus päällä" : "Äänen tasoitus pois");
        RestartRequested?.Invoke();
    }

    // ---------- Suoratoisto (Chromecast) ----------

    /// <summary>Laitteen nimi, johon suoratoistetaan (null = tällä koneella).</summary>
    [ObservableProperty] private string? _castTarget;

    public LibVLC LibVLC => _libvlc;

    public void CastTo(RendererItem? item)
    {
        CastTarget = item?.Name;
        try { _player.SetRenderer(item); } catch { }
        ShowOsd(item is null ? "Suoratoisto lopetettu – toisto tällä koneella" : $"Suoratoistetaan: {item.Name}");
        // Renderöijä otetaan käyttöön median uudelleenavauksessa
        RestartRequested?.Invoke();
    }

    // ---------- Ilmoitus kuvan päällä (OSD) ----------

    [ObservableProperty] private string _osd = "";
    private DispatcherTimer? _osdTimer;

    public void ShowOsd(string text)
    {
        Osd = text;
        _osdTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2.5), DispatcherPriority.Normal, (_, _) => { Osd = ""; _osdTimer!.Stop(); }, Dispatcher.CurrentDispatcher);
        _osdTimer.Stop();
        _osdTimer.Start();
    }

    private void OnTracksChanged(object? sender, MediaPlayerESAddedEventArgs e) => _tracksDirty = true;
    private void OnTracksChanged(object? sender, MediaPlayerESDeletedEventArgs e) => _tracksDirty = true;
    private void OnTracksChanged(object? sender, MediaPlayerESSelectedEventArgs e) => _tracksDirty = true;

    private void OnStopped(object? sender, EventArgs e) => Ui(Reset);

    /// <summary>Kutsutaan, kun uusi striimi käynnistetään.</summary>
    public void Reset()
    {
        AudioTracks = [];
        SubtitleTracks = [];
        SelectedAudioId = -1;
        SelectedSubtitleId = -1;
        QualityBadge = FormatText = BitrateText = DataText = "";
        HasStats = false;
        _bitrateAvg = 0;
        _lastReadBytes = 0;
        _tracksDirty = true;
        _autoPicked = false;
    }

    private static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    // ---------- Äänenvoimakkuus ----------

    partial void OnVolumeChanged(int value)
    {
        if (value > 0 && IsMuted) IsMuted = false;
        ApplyVolume();
        _settings.Settings.Volume = value;
        OnPropertyChanged(nameof(VolumeGlyph));
    }

    partial void OnIsMutedChanged(bool value)
    {
        ApplyVolume();
        OnPropertyChanged(nameof(VolumeGlyph));
    }

    private void ApplyVolume()
    {
        try
        {
            _player.Mute = IsMuted;
            _player.Volume = Volume;
        }
        catch { /* soitin ei vielä valmis */ }
    }

    [RelayCommand] private void ToggleMute() => IsMuted = !IsMuted;
    [RelayCommand] private void VolumeUp() => Volume = Math.Min(100, Volume + 5);
    [RelayCommand] private void VolumeDown() => Volume = Math.Max(0, Volume - 5);

    public string VolumeGlyph => IsMuted || Volume == 0 ? "" : Volume < 34 ? "" : Volume < 67 ? "" : "";

    // ---------- Raidat ----------

    public void SelectAudio(int id)
    {
        SelectedAudioId = id;
        var lang = AudioTracks.FirstOrDefault(t => t.Id == id)?.Language;
        if (lang is not null) { _settings.Settings.PreferredAudioLanguage = lang; SaveSettings(); }
        Task.Run(() => _player.SetAudioTrack(id));
    }

    public void SelectSubtitle(int id)
    {
        SelectedSubtitleId = id;
        _settings.Settings.PreferredSubtitleLanguage = id < 0 ? "" : SubtitleTracks.FirstOrDefault(t => t.Id == id)?.Language ?? "";
        SaveSettings();
        Task.Run(() => _player.SetSpu(id));
    }

    private void SaveSettings()
    {
        try { _settings.Save(); } catch { }
    }

    private void RefreshTracks()
    {
        _tracksDirty = false;
        MediaTrack[] media = [];
        try
        {
            using var m = _player.Media;
            if (m is not null) media = m.Tracks;
        }
        catch { }

        string? LangOf(int id) => media.FirstOrDefault(t => t.Id == id).Language is { Length: > 0 } l ? l : null;

        var audio = _player.AudioTrackDescription
            .Where(d => d.Id >= 0)
            .Select((d, i) => new TrackOption(d.Id, Label(d.Name, LangOf(d.Id), $"Ääniraita {i + 1}"), LangOf(d.Id) ?? ParseLang(d.Name)))
            .ToList();
        var subs = new List<TrackOption> { new(-1, "Ei tekstitystä", null) };
        subs.AddRange(_player.SpuDescription
            .Where(d => d.Id >= 0)
            .Select((d, i) => new TrackOption(d.Id, Label(d.Name, LangOf(d.Id), $"Tekstitys {i + 1}"), LangOf(d.Id) ?? ParseLang(d.Name))));

        if (!audio.SequenceEqual(AudioTracks)) AudioTracks = audio;
        if (!subs.SequenceEqual(SubtitleTracks)) SubtitleTracks = subs;
        SelectedAudioId = _player.AudioTrack;
        SelectedSubtitleId = _player.Spu;

        // Valitaan käyttäjän aiemmin valitsemat kielet automaattisesti
        if (!_autoPicked && audio.Count > 0)
        {
            _autoPicked = true;
            var s = _settings.Settings;
            if (!string.IsNullOrEmpty(s.PreferredAudioLanguage) && audio.Count > 1 &&
                audio.FirstOrDefault(t => SameLang(t.Language, s.PreferredAudioLanguage)) is { } a && a.Id != SelectedAudioId)
            {
                SelectedAudioId = a.Id;
                Task.Run(() => _player.SetAudioTrack(a.Id));
            }
            if (!string.IsNullOrEmpty(s.PreferredSubtitleLanguage) &&
                subs.FirstOrDefault(t => t.Id >= 0 && SameLang(t.Language, s.PreferredSubtitleLanguage)) is { } sp && sp.Id != SelectedSubtitleId)
            {
                SelectedSubtitleId = sp.Id;
                Task.Run(() => _player.SetSpu(sp.Id));
            }
        }
    }

    private static bool SameLang(string? a, string? b) =>
        a is not null && b is not null && string.Equals(LanguageName(a), LanguageName(b), StringComparison.OrdinalIgnoreCase);

    private static string? ParseLang(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var m = Regex.Match(name, @"\[(?<l>[^\]]+)\]");
        return m.Success ? m.Groups["l"].Value : null;
    }

    /// <summary>Muodostaa selkeän nimen: "Suomi", "Englanti (Dolby)" jne.</summary>
    private static string Label(string? vlcName, string? lang, string fallback)
    {
        var language = LanguageName(lang ?? ParseLang(vlcName));
        // VLC:n nimi on usein "Track 1 - [English]" tai "Raita 1 - [Suomi]"
        var desc = vlcName is null ? "" : Regex.Replace(vlcName, @"^(Track|Raita|Ääniraita)\s*\d+\s*(-\s*)?", "", RegexOptions.IgnoreCase);
        desc = Regex.Replace(desc, @"\[[^\]]*\]", "").Trim(' ', '-');
        if (language is not null && desc.Length > 0 && !desc.Equals(language, StringComparison.OrdinalIgnoreCase)) return $"{language} ({desc})";
        if (language is not null) return language;
        return desc.Length > 0 ? desc : fallback;
    }

    private static readonly Dictionary<string, string> LangCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ISO 639 -koodi (fin, en, swe…) tai nimi → suomenkielinen kielen nimi.</summary>
    public static string? LanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        code = code.Trim();
        if (LangCache.TryGetValue(code, out var cached)) return cached;
        string result = code;
        try
        {
            var fi = CultureInfo.GetCultureInfo("fi-FI");
            foreach (var c in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
            {
                if (c.Name.Length == 0) continue;
                if (c.TwoLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase) ||
                    c.ThreeLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase) ||
                    c.EnglishName.Equals(code, StringComparison.OrdinalIgnoreCase) ||
                    c.NativeName.Equals(code, StringComparison.OrdinalIgnoreCase))
                {
                    var n = c.NativeName;
                    // Näytetään suomeksi tutut kielet, muut omalla nimellään
                    result = code.ToLowerInvariant() switch
                    {
                        "fin" or "fi" or "finnish" or "suomi" => "Suomi",
                        "swe" or "sv" or "swedish" or "svenska" => "Ruotsi",
                        "eng" or "en" or "english" => "Englanti",
                        "nor" or "no" or "nob" or "nb" or "norwegian" => "Norja",
                        "dan" or "da" or "danish" => "Tanska",
                        "ger" or "deu" or "de" or "german" => "Saksa",
                        "fre" or "fra" or "fr" or "french" => "Ranska",
                        "spa" or "es" or "spanish" => "Espanja",
                        "rus" or "ru" or "russian" => "Venäjä",
                        "est" or "et" or "estonian" => "Viro",
                        _ => char.ToUpper(n[0], fi) + n[1..],
                    };
                    break;
                }
            }
        }
        catch { }
        LangCache[code] = result;
        return result;
    }

    // ---------- Tilastot ----------

    private void Poll()
    {
        if (!_player.IsPlaying && _player.State != VLCState.Paused) { if (HasStats) HasStats = false; return; }
        if (_tracksDirty) RefreshTracks();

        try
        {
            using var m = _player.Media;
            if (m is null) return;
            var st = m.Statistics;
            // libvlc: input_bitrate on tavuina/ms → Mbit/s = arvo * 8
            var mbps = st.InputBitrate * 8.0;
            if (mbps <= 0 && _lastReadBytes > 0) mbps = (st.ReadBytes - _lastReadBytes) * 8.0 / 1_000_000;
            _lastReadBytes = st.ReadBytes;
            _bitrateAvg = _bitrateAvg <= 0 ? mbps : _bitrateAvg * 0.7 + mbps * 0.3;
            BitrateText = _bitrateAvg > 0 ? _bitrateAvg.ToString(_bitrateAvg >= 10 ? "0" : "0.0", CultureInfo.GetCultureInfo("fi-FI")) + " Mbit/s" : "";
            DataText = st.ReadBytes > 0 ? "Ladattu " + FormatBytes(st.ReadBytes) : "";

            uint w = 0, h = 0;
            string vcodec = "", acodec = "";
            foreach (var t in m.Tracks)
            {
                if (t.TrackType == TrackType.Video && w == 0)
                {
                    w = t.Data.Video.Width;
                    h = t.Data.Video.Height;
                    vcodec = CodecName(TrackType.Video, t.Codec);
                }
                else if (t.TrackType == TrackType.Audio && acodec.Length == 0 && t.Id == _player.AudioTrack)
                    acodec = CodecName(TrackType.Audio, t.Codec);
            }
            if (w == 0) _player.Size(0, ref w, ref h);
            var fps = _player.Fps;

            QualityBadge = h switch
            {
                0 => "",
                >= 2000 => "4K",
                >= 1400 => "1440p",
                >= 1000 => "1080p",
                >= 700 => "720p",
                >= 560 => "576p",
                _ => $"{h}p",
            };
            var parts = new List<string>();
            if (w > 0) parts.Add($"{w}×{h}");
            if (fps > 1) parts.Add($"{Math.Round(fps):0} fps");
            if (vcodec.Length > 0) parts.Add(vcodec);
            if (acodec.Length > 0) parts.Add(acodec);
            FormatText = string.Join(" · ", parts);
            HasStats = QualityBadge.Length > 0 || BitrateText.Length > 0;
        }
        catch { }
    }

    private string CodecName(TrackType type, uint fourcc)
    {
        var name = fourcc switch
        {
            0x34363268 => "H.264", // h264
            0x35363268 => "HEVC",  // h265
            0x63766568 => "HEVC",  // hevc
            0x31307661 => "AV1",
            0x7632706d => "MPEG-2", // mp2v
            0x6134706d => "AAC",    // mp4a
            0x20323561 => "AC-3",   // "a52 "
            0x33636165 => "E-AC-3", // eac3
            0x6167706d => "MPEG Audio", // mpga
            0x7375706f => "Opus",
            _ => null,
        };
        return name ?? FourCC(fourcc);
    }

    private static string FourCC(uint v) =>
        new string([(char)(v & 0xFF), (char)((v >> 8) & 0xFF), (char)((v >> 16) & 0xFF), (char)((v >> 24) & 0xFF)]).Trim().ToUpperInvariant();

    private static string FormatBytes(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.00} Gt" : b >= 1 << 20 ? $"{b / (double)(1 << 20):0} Mt" : $"{b / 1024.0:0} kt";

    public void Dispose()
    {
        _stats.Stop();
        _player.Playing -= OnPlaying;
        _player.ESAdded -= OnTracksChanged;
        _player.ESDeleted -= OnTracksChanged;
        _player.ESSelected -= OnTracksChanged;
        _player.Stopped -= OnStopped;
    }
}
