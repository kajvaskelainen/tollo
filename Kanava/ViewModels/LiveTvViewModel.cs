using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanava.Models;
using Kanava.Services;

namespace Kanava.ViewModels;

public partial class LiveTvViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly DispatcherTimer _clock;
    private CancellationTokenSource? _epgCts;
    private List<LiveChannel> _all = [];

    public ObservableCollection<Category> Categories { get; } = [];

    [ObservableProperty] private Category? _selectedCategory;
    [ObservableProperty] private IReadOnlyList<LiveChannel> _channels = [];
    [ObservableProperty] private LiveChannel? _selectedChannel;
    [ObservableProperty] private LiveChannel? _playingChannel;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _selectedTab; // 0 = Kanavakortit, 1 = TV-opas

    [ObservableProperty] private EpgProgram? _nowProgram;
    [ObservableProperty] private EpgProgram? _nextProgram;
    [ObservableProperty] private double _nowProgress;
    [ObservableProperty] private DateTime _now = DateTime.Now;
    [ObservableProperty] private string _guideStatus = "";
    [ObservableProperty] private bool _isFullscreen;
    /// <summary>Tallennetaanko toistettavaa kanavaa (REC-merkki).</summary>
    [ObservableProperty] private bool _isRecordingPlaying;
    /// <summary>Asettaa MainViewModel: onko kanavalla käynnissä tallennus.</summary>
    public Func<int, bool>? IsRecording { get; set; }
    public void RefreshRecording() => IsRecordingPlaying = PlayingChannel is { } c && IsRecording?.Invoke(c.StreamId) == true;
    partial void OnPlayingChannelChanged(LiveChannel? value) => RefreshRecording();

    /// <summary>Esim. "Yhteys katkesi – yhdistetään uudelleen (2/8)…" – näytetään kuvan päällä.</summary>
    [ObservableProperty] private string _streamStatus = "";
    /// <summary>Kasvaa aina kun opas päivittyy (EPG-ruudukko piirtää uudelleen).</summary>
    [ObservableProperty] private int _guideVersion;

    public LiveTvViewModel(AppSession session)
    {
        _session = session;
        Playback = new PlaybackViewModel(session.Player.Preview, session.Player.LibVLC, session.Settings);
        Playback.RestartRequested = () => _session.Player.Restart(ActivePlayer);
        session.Player.Reconnecting += OnReconnecting;
        session.Player.ReconnectFailed += OnReconnectFailed;
        session.Player.Preview.Playing += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => StreamStatus = "");
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _clock.Tick += (_, _) => Tick();
        _clock.Start();
    }

    public AppSession Session => _session;

    /// <summary>Esikatselusoittimen hallinta (ääni, raidat, lähetystiedot).</summary>
    public PlaybackViewModel Playback { get; }

    public void Load()
    {
        _all = _session.VisibleLiveChannels.ToList();
        Categories.Clear();
        Categories.Add(new Category { Id = "__history", Name = "Historia", Special = SpecialCategory.History });
        Categories.Add(new Category { Id = "__fav", Name = "Suosikit", Special = SpecialCategory.Favorites });
        Categories.Add(new Category { Id = "__all", Name = "Kaikki kanavat", Special = SpecialCategory.All });
        foreach (var c in CategoryOrder.Ordered()) Categories.Add(c);
        SelectedCategory = Categories[2];
    }

    /// <summary>
    /// Rakentaa listat uudelleen kanavien hallinnan jälkeen (piilotus, järjestys, uudelleenladattu
    /// kanavalista). Valittu kategoria säilyy, jos se on yhä näkyvissä.
    /// </summary>
    public void Reload()
    {
        var selectedId = SelectedCategory?.Id;
        _all = _session.VisibleLiveChannels.ToList();
        var specials = Categories.Where(c => c.Special != SpecialCategory.None).ToList();
        Categories.Clear();
        foreach (var c in specials) Categories.Add(c);
        foreach (var c in CategoryOrder.Ordered()) Categories.Add(c);
        var sel = Categories.FirstOrDefault(c => c.Id == selectedId) ?? Categories.First(c => c.Special == SpecialCategory.All);
        if (SelectedCategory == sel) ApplyFilter(); else SelectedCategory = sel;
        UpdateCurrentPrograms();
        GuideVersion++;
    }

    private CategoryOrderer? _categoryOrder;
    public CategoryOrderer CategoryOrder => _categoryOrder ??= new(Categories, _session.UserData, "live", () => _session.VisibleLiveCategories)
    {
        GetSelected = () => SelectedCategory,
        SetSelected = c => SelectedCategory = c,
        SaveOrder = ids => _session.SaveLiveCategoryOrder(ids),
    };

    partial void OnSelectedCategoryChanged(Category? value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private ObservableCollection<LiveChannel> _channelList = [];

    /// <summary>Tallennetun järjestyksen avain valitulle kategorialle (null = ei järjestettävissä).</summary>
    private string? OrderKey => SelectedCategory switch
    {
        null => null,
        { Special: SpecialCategory.History } => null,
        { Special: SpecialCategory.Favorites } => "__fav",
        { Special: SpecialCategory.All } => "__all",
        var c => c.Id,
    };

    public void ApplyFilter()
    {
        IEnumerable<LiveChannel> q;
        var cat = SelectedCategory;
        var ud = _session.UserData;
        if (cat?.Special == SpecialCategory.History)
        {
            var byId = _session.LiveById;
            q = ud.LiveHistory.Select(id => byId.GetValueOrDefault(id)).OfType<LiveChannel>().Where(_session.IsVisible);
        }
        else q = _session.OrderedChannels(OrderKey ?? "__all", includeHidden: false);

        var s = SearchText.Trim();
        if (s.Length > 0) q = q.Where(c => c.Name.Contains(s, StringComparison.CurrentCultureIgnoreCase));
        _channelList = new ObservableCollection<LiveChannel>(q);
        Channels = _channelList;
        OnPropertyChanged(nameof(CanReorderChannels));
    }

    // ---------- Järjestely ----------

    /// <summary>Järjestystila: klikkaus ei käynnistä toistoa, kortteja ja kategorioita voi raahata.</summary>
    [ObservableProperty] private bool _isReorderMode;

    public bool CanReorderChannels => OrderKey is not null && SearchText.Trim().Length == 0;

    partial void OnIsReorderModeChanged(bool value)
    {
        if (value) SelectedTab = 0;
    }

    [RelayCommand]
    private void ToggleReorder() => IsReorderMode = !IsReorderMode;

    /// <summary>Siirtää kanavan kohteen paikalle (raahaus).</summary>
    public void MoveChannel(LiveChannel item, LiveChannel? target)
    {
        if (!CanReorderChannels) return;
        var from = _channelList.IndexOf(item);
        var to = target is null ? _channelList.Count - 1 : _channelList.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        _channelList.Move(from, to);
        SaveChannelOrder();
    }

    [RelayCommand]
    private void MoveChannelToTop(LiveChannel? c) => MoveChannelTo(c, 0);

    [RelayCommand]
    private void MoveChannelToBottom(LiveChannel? c) => MoveChannelTo(c, _channelList.Count - 1);

    private void MoveChannelTo(LiveChannel? c, int index)
    {
        if (c is null || !CanReorderChannels) return;
        var from = _channelList.IndexOf(c);
        if (from < 0 || from == index) return;
        _channelList.Move(from, index);
        SaveChannelOrder();
    }

    private void SaveChannelOrder()
    {
        if (OrderKey is not { } key) return;
        // Lista sisältää vain näkyvät kanavat; piilotetut pysyvät paikoillaan (eivät putoa suosikeista)
        _session.SaveChannelOrder(key, _channelList.Select(c => c.StreamId).ToList());
    }

    [RelayCommand]
    private void ResetChannelOrder()
    {
        if (OrderKey is not { } key) return;
        if (key == "__fav")
            _session.UserData.SetChannelOrder(key, _session.LiveChannels.Where(c => c.IsFavorite).Select(c => c.StreamId));
        else
            _session.UserData.SetChannelOrder(key, null);
        ApplyFilter();
    }

    partial void OnSelectedChannelChanged(LiveChannel? value)
    {
        if (value is not null && !IsReorderMode) Play(value);
    }

    /// <summary>Käynnistää kanavan esikatselun.</summary>
    public void Play(LiveChannel channel)
    {
        if (PlayingChannel == channel) return;
        if (PlayingChannel is { } prev) _previousChannel = prev;
        PlayingChannel = channel;
        StreamStatus = "";
        Playback.Reset();
        Playback.SetSyncKey(SyncKey(channel));
        _session.Player.Play(ActivePlayer, LiveUrl(channel));
        _session.UserData.AddLiveHistory(channel.StreamId);
        _ = LoadNowNextAsync(channel);
    }

    public static string SyncKey(LiveChannel c) => $"live:{c.StreamId}";

    private void OnReconnecting(LibVLCSharp.Shared.MediaPlayer player, int attempt, int max) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (player == ActivePlayer && PlayingChannel is not null) StreamStatus = $"Yhteys katkesi – yhdistetään uudelleen ({attempt}/{max})…";
        });

    private void OnReconnectFailed(LibVLCSharp.Shared.MediaPlayer player) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (player == ActivePlayer && PlayingChannel is not null) StreamStatus = "Lähetys ei vastaa. Valitse kanava uudelleen tai kokeile myöhemmin.";
        });

    // ---------- Kanavanvaihto näppäimillä ----------

    private LiveChannel? _previousChannel;
    private string _numberBuffer = "";
    private DispatcherTimer? _numberTimer;

    /// <summary>Numeronäppäin: kerätään kanavanumero ja vaihdetaan 1,5 s tauon jälkeen.</summary>
    public void TypeDigit(int digit)
    {
        if (_numberBuffer.Length >= 5) _numberBuffer = "";
        _numberBuffer += digit.ToString();
        Playback.ShowOsd($"Kanava {_numberBuffer}–");
        _numberTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Normal, (_, _) => CommitNumber(), Dispatcher.CurrentDispatcher);
        _numberTimer.Stop();
        _numberTimer.Start();
    }

    private void CommitNumber()
    {
        _numberTimer?.Stop();
        var text = _numberBuffer;
        _numberBuffer = "";
        if (!int.TryParse(text, out var n)) return;
        var ch = _all.FirstOrDefault(c => c.Number == n);
        if (ch is null) { Playback.ShowOsd($"Kanavaa {n} ei löydy"); return; }
        SelectAndPlay(ch);
    }

    /// <summary>Valitsee kanavan; jos se ei ole nykyisessä listassa, vaihdetaan "Kaikki kanavat" -näkymään.</summary>
    public void SelectAndPlay(LiveChannel ch)
    {
        if (!Channels.Contains(ch) && Categories.FirstOrDefault(c => c.Special == SpecialCategory.All) is { } all)
        {
            SearchText = "";
            SelectedCategory = all;
        }
        SelectedChannel = ch;
        if (PlayingChannel != ch) Play(ch);
    }

    /// <summary>Edellinen katsottu kanava (Backspace).</summary>
    public void PreviousChannel()
    {
        if (_previousChannel is { } p && p != PlayingChannel && _all.Contains(p)) SelectAndPlay(p);
        else Playback.ShowOsd("Ei edellistä kanavaa");
    }

    // ---------- Jälkikatselu ----------

    /// <summary>Toistopyyntö menneelle (tai käynnissä olevalle, alusta) ohjelmalle palvelimen arkistosta.</summary>
    public PlaybackRequest CatchupRequest(LiveChannel ch, EpgProgram p)
    {
        var minutes = (int)Math.Ceiling((p.End - p.Start).TotalMinutes) + 2;
        return new PlaybackRequest
        {
            Url = _session.Client.TimeshiftUrl(ch.StreamId, p.Start, minutes),
            Title = $"{ch.Name} – {p.Title} ({p.Start:d.M. HH.mm})",
            SyncKey = SyncKey(ch),
        };
    }

    /// <summary>Pysäyttää toiston kokonaan (uniajastin).</summary>
    public void StopPlayback()
    {
        IsFullscreen = false;
        _session.Player.Stop(ActivePlayer);
        SelectedChannel = null;
        PlayingChannel = null;
        StreamStatus = "";
    }

    private string LiveUrl(LiveChannel c) => _session.Client.LiveUrl(c.StreamId, _session.Settings.Settings.LiveFormat);

    // ---------- Pienoissoitin (PiP) ----------

    private LibVLCSharp.Shared.MediaPlayer? _pipPlayer;
    /// <summary>Soitin, johon kanavanvaihdot ohjataan: pienoissoitin, jos se on auki, muuten esikatselu.</summary>
    private LibVLCSharp.Shared.MediaPlayer ActivePlayer => _pipPlayer ?? _session.Player.Preview;
    public bool IsPip => _pipPlayer is not null;

    /// <summary>Siirtää toiston pienoissoittimen omaan soittimeen (esikatselu pysähtyy).</summary>
    public void EnterPip(LibVLCSharp.Shared.MediaPlayer player)
    {
        _pipPlayer = player;
        _session.Player.Stop(_session.Player.Preview);
        if (PlayingChannel is { } ch) _session.Player.Play(player, LiveUrl(ch));
    }

    /// <summary>Palauttaa toiston esikatseluun (resume = false: ei toistoa, esim. kun PiP suljetaan ikkunan ollessa piilossa).</summary>
    public void ExitPip(bool resume)
    {
        if (_pipPlayer is null) return;
        _pipPlayer = null;
        if (resume && PlayingChannel is { } ch) _session.Player.Play(_session.Player.Preview, LiveUrl(ch));
    }

    /// <summary>Seuraava/edellinen kanava nykyisestä listasta (pienoissoittimen napit).</summary>
    public void PlayAdjacent(int dir)
    {
        var list = Channels;
        if (list.Count == 0) return;
        var i = PlayingChannel is null ? -1 : IndexOf(list, PlayingChannel);
        var next = list[((i < 0 ? (dir > 0 ? -1 : 0) : i) + dir + list.Count) % list.Count];
        SelectedChannel = next; // käynnistää toiston (OnSelectedChannelChanged)
        if (IsReorderMode) Play(next);
    }

    private static int IndexOf(IReadOnlyList<LiveChannel> list, LiveChannel c)
    {
        for (var i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], c)) return i;
        return -1;
    }

    [RelayCommand]
    private void ToggleFullscreen() => IsFullscreen = !IsFullscreen && PlayingChannel is not null;

    /// <summary>Tuplaklikkaus kanavaan: toista ja siirry koko näyttöön.</summary>
    [RelayCommand]
    private void PlayFullscreen(LiveChannel? channel)
    {
        if (channel is null) return;
        SelectedChannel = channel;
        Play(channel);
        IsFullscreen = true;
    }

    /// <summary>Pysäyttää esikatselun (esim. kun poistutaan Live TV -näkymästä).</summary>
    public void Suspend()
    {
        IsFullscreen = false;
        _session.Player.Stop(_session.Player.Preview);
    }

    public void Resume()
    {
        // Suosikit/historia ovat voineet muuttua muualla
        if (SelectedCategory?.Special is SpecialCategory.Favorites or SpecialCategory.History) ApplyFilter();
        if (PlayingChannel is { } ch)
            _session.Player.Play(_session.Player.Preview, LiveUrl(ch));
    }

    [RelayCommand]
    private void ToggleFavorite(LiveChannel? channel)
    {
        if (channel is null) return;
        _session.UserData.ToggleLiveFavorite(channel);
        if (SelectedCategory?.Special == SpecialCategory.Favorites)
        {
            // Ei koko listan uudelleenrakennusta: poistetaan/lisätään vain kyseinen kanava
            if (!channel.IsFavorite) _channelList.Remove(channel);
            else if (!_channelList.Contains(channel)) _channelList.Add(channel);
        }
    }

    private async Task LoadNowNextAsync(LiveChannel channel)
    {
        _epgCts?.Cancel();
        var cts = _epgCts = new CancellationTokenSource();
        // Näytetään heti XMLTV-välimuistin tiedot, jos ne on jo ladattu
        UpdateNowNextFromGuide(channel);
        try
        {
            var list = await _session.Client.GetShortEpgAsync(channel.StreamId, 4, cts.Token);
            if (cts.IsCancellationRequested || PlayingChannel != channel || list.Count == 0) return;
            SetNowNext(list);
        }
        catch { /* lyhyt EPG ei ole kriittinen */ }
    }

    private void UpdateNowNextFromGuide(LiveChannel channel)
    {
        var list = _session.Epg.GetPrograms(channel);
        SetNowNext(list);
    }

    private void SetNowNext(IReadOnlyList<EpgProgram> list)
    {
        var now = DateTime.Now;
        var idx = -1;
        for (var i = 0; i < list.Count; i++)
            if (list[i].IsNow(now)) { idx = i; break; }
        NowProgram = idx >= 0 ? list[idx] : null;
        NextProgram = idx >= 0 && idx + 1 < list.Count ? list[idx + 1] : list.FirstOrDefault(p => p.Start > now);
        NowProgress = NowProgram?.Progress(now) ?? 0;
    }

    /// <summary>Päivittää kanavakorttien "nyt"-ohjelmat oppaasta.</summary>
    private void UpdateCurrentPrograms()
    {
        if (!_session.Epg.IsLoaded) return;
        var now = DateTime.Now;
        foreach (var ch in _all)
        {
            var cur = ch.Current;
            if (cur is null || !cur.IsNow(now)) cur = FindCurrent(_session.Epg.GetPrograms(ch), now);
            ch.Current = cur;
            ch.CurrentProgress = cur?.Progress(now) ?? 0;
        }
    }

    public static EpgProgram? FindCurrent(IReadOnlyList<EpgProgram> list, DateTime now)
    {
        int lo = 0, hi = list.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var p = list[mid];
            if (p.End <= now) lo = mid + 1;
            else if (p.Start > now) hi = mid - 1;
            else return p;
        }
        return null;
    }

    private void Tick()
    {
        Now = DateTime.Now;
        UpdateCurrentPrograms();
        if (NowProgram is not null)
        {
            if (Now >= NowProgram.End && PlayingChannel is not null)
            {
                var ch = PlayingChannel;
                PlayingChannel = null; // pakottaa uudelleenhaun
                PlayingChannel = ch;
                _ = LoadNowNextAsync(ch);
            }
            else NowProgress = NowProgram.Progress(Now);
        }
    }

    /// <summary>Kutsutaan, kun XMLTV-opas on ladattu taustalla.</summary>
    public void OnGuideLoaded()
    {
        GuideStatus = _session.Epg.Status;
        UpdateCurrentPrograms();
        GuideVersion++;
        if (PlayingChannel is not null && NowProgram is null) UpdateNowNextFromGuide(PlayingChannel);
    }
}
