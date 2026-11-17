using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Kanava.Models;
using Kanava.Services;
using Kanava.ViewModels;
using LibVLCSharp.Shared;

namespace Kanava.Views;

/// <summary>
/// Elokuvien, jaksojen ja jälkikatselun toisto koko näytössä. Tallentaa katselukohdan
/// (jatka katsomista), jatkaa katkoksen jälkeen samasta kohdasta ja tarjoaa sarjan seuraavaa jaksoa.
/// </summary>
public partial class VodPlayerWindow : Window
{
    private const int MaxRetries = 4;

    private readonly PlayerService _service;
    private readonly SettingsStore _settings;
    private readonly UserDataStore? _userData;
    private readonly MediaPlayer _player;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _nextTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly PlaybackViewModel _playback;
    private PlaybackRequest _req = null!;
    private bool _seeking;
    private float _lastPosition;
    private long _lastTimeMs, _lengthMs, _resumeAt;
    private int _retries, _ticks, _nextCountdown;
    private bool _hasPlayed;
    private Point _lastMouse;

    public VodPlayerWindow(PlayerService service, SettingsStore settings, UserDataStore? userData, PlaybackRequest request)
    {
        InitializeComponent();
        _service = service;
        _settings = settings;
        _userData = userData;
        _player = service.CreatePlayer();

        _player.Playing += (_, _) => Dispatcher.BeginInvoke(OnPlaying);
        _player.Paused += (_, _) => Dispatcher.BeginInvoke(() => PlayPause.Content = "");
        _player.Buffering += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            if (_retries > 0 && !_hasPlayed) return; // uudelleenyhdistyksen teksti pysyy
            StatusText.Text = $"Puskuroidaan… {e.Cache:0} %";
            StatusText.Visibility = e.Cache < 100 ? Visibility.Visible : Visibility.Collapsed;
        });
        _player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() => OnBroken(error: true));
        _player.EndReached += (_, _) => Dispatcher.BeginInvoke(OnEndReached);

        _tick.Tick += (_, _) => UpdatePosition();
        _hide.Tick += (_, _) => HideControls();
        _nextTimer.Tick += (_, _) => NextTick();

        _playback = new PlaybackViewModel(_player, service.LibVLC, settings)
        {
            RestartRequested = () =>
            {
                // Äänen tasoitus vaatii median uudelleenavauksen – jatketaan samasta kohdasta
                _resumeAt = _player.Time;
                _service.Play(_player, _req.Url, live: false);
            },
        };
        Overlay.DataContext = _playback;
        Loaded += (_, _) =>
        {
            Video.MediaPlayer = _player;
            _player.Volume = _playback.Volume;
            Load(request);
            _tick.Start();
            ShowControls();
        };
        PreviewKeyDown += OnKey;
        // Overlay on oma ikkunansa (VideoView) – kuunnellaan näppäimiä sieltäkin
        Overlay.Loaded += (_, _) =>
        {
            if (GetWindow(Overlay) is { } w && w != this) w.PreviewKeyDown += OnKey;
        };
    }

    /// <summary>Aloittaa (uuden) toiston samassa ikkunassa – myös seuraava jakso.</summary>
    private void Load(PlaybackRequest req)
    {
        _req = req;
        TitleText.Text = req.Title;
        Title = req.Title + " – Töllö";
        NextButton.Visibility = req.Next is not null ? Visibility.Visible : Visibility.Collapsed;
        NextButton.ToolTip = req.NextLabel is { } nl ? $"Seuraava jakso: {nl} (N)" : "Seuraava jakso (N)";
        HideNextCard();
        _resumeAt = req.ResumeMs;
        _retries = 0;
        _lastTimeMs = _lengthMs = 0;
        _lastPosition = 0;
        _hasPlayed = false;
        Seek.Value = 0;
        StatusText.Text = "Ladataan…";
        StatusText.Visibility = Visibility.Visible;
        _playback.Reset();
        _playback.SetSyncKey(req.SyncKey ?? req.ProgressKey ?? "vod:" + Hash(req.Url));
        _service.Play(_player, req.Url, live: false);
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..12];

    private void OnPlaying()
    {
        StatusText.Visibility = Visibility.Collapsed;
        PlayPause.Content = "";
        _hasPlayed = true;
        if (_resumeAt > 0)
        {
            var at = _resumeAt;
            _resumeAt = 0;
            Task.Run(() => { try { _player.Time = at; } catch { } });
            if (_retries == 0) _playback.ShowOsd($"Jatketaan kohdasta {Fmt(at)} – Home palaa alkuun");
        }
    }

    // ---------- Katkokset ja loppu ----------

    private bool IsAtEnd => _lengthMs > 0 && (_lastPosition > 0.97f || _lastTimeMs >= _lengthMs - 15_000);

    private void OnEndReached()
    {
        if (IsAtEnd) { Finished(); return; }
        OnBroken(error: false);
    }

    /// <summary>Virhe tai ennenaikainen loppu: yhdistetään uudelleen ja jatketaan samasta kohdasta.</summary>
    private void OnBroken(bool error)
    {
        if (_hasPlayed && _retries < MaxRetries)
        {
            _retries++;
            _resumeAt = _lastTimeMs;
            _hasPlayed = false;
            StatusText.Text = $"Yhteys katkesi – yhdistetään uudelleen ({_retries}/{MaxRetries})…";
            StatusText.Visibility = Visibility.Visible;
            var req = _req;
            var delay = TimeSpan.FromSeconds(_retries * 2);
            Task.Delay(delay).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                if (_req == req && IsLoaded) _service.Play(_player, req.Url, live: false);
            }));
            return;
        }
        SaveProgress();
        StatusText.Text = _hasPlayed || _retries > 0 ? "Yhteys katkesi. Yritä myöhemmin uudelleen – katselukohta on tallessa."
                                                     : error ? "Toisto epäonnistui." : "Toisto epäonnistui – tiedostoa ei voitu avata.";
        StatusText.Visibility = Visibility.Visible;
        PlayPause.Content = "";
        ShowControls();
    }

    private void Finished()
    {
        if (_req.ProgressKey is { } key) _userData?.SetWatched(key, true);
        if (_req.Next is null) { Close(); return; }
        _nextCountdown = 10;
        NextTitle.Text = "Seuraava jakso: " + (_req.NextLabel ?? "");
        NextCountdown.Text = $"Alkaa {_nextCountdown} s kuluttua";
        NextCard.Visibility = Visibility.Visible;
        _nextTimer.Start();
        ShowControls();
    }

    private void NextTick()
    {
        _nextCountdown--;
        if (_nextCountdown <= 0) { PlayNext(); return; }
        NextCountdown.Text = $"Alkaa {_nextCountdown} s kuluttua";
    }

    private void HideNextCard()
    {
        _nextTimer.Stop();
        NextCard.Visibility = Visibility.Collapsed;
    }

    private void PlayNext()
    {
        HideNextCard();
        SaveProgress();
        var next = _req.Next?.Invoke();
        if (next is null) { Close(); return; }
        Load(next);
    }

    private void OnPlayNext(object sender, RoutedEventArgs e) => PlayNext();
    private void OnCancelNext(object sender, RoutedEventArgs e) => HideNextCard();

    // ---------- Katselukohta ----------

    private void SaveProgress()
    {
        if (_userData is null || _req.ProgressKey is not { } key || _lengthMs <= 0 || _lastTimeMs <= 0) return;
        _userData.SetProgress(key, _lastTimeMs, _lengthMs, new WatchProgress
        {
            Title = _req.Title,
            SeriesId = _req.SeriesId,
            Season = _req.Season,
            Episode = _req.Episode,
        });
    }

    // ---------- Näppäimet ----------

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Space: TogglePause(); break;
            case Key.Left: SeekBy(-10); break;
            case Key.Right: SeekBy(30); break;
            case Key.Up: _playback.VolumeUpCommand.Execute(null); break;
            case Key.Down: _playback.VolumeDownCommand.Execute(null); break;
            case Key.M: _playback.ToggleMuteCommand.Execute(null); break;
            case Key.J: _playback.ShiftAudio(-100); break;
            case Key.K: _playback.ShiftAudio(100); break;
            case Key.Home: SeekTo(0); _playback.ShowOsd("Alusta"); break;
            case Key.N when _req.Next is not null: PlayNext(); break;
            default: return;
        }
        ShowControls();
        e.Handled = true;
    }

    private static string Fmt(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private void UpdatePosition()
    {
        var len = _player.Length;
        if (len > 0) _lengthMs = len;
        LenText.Text = len > 0 ? Fmt(len) : "--:--";
        var time = _player.Time;
        PosText.Text = Fmt(time);
        if (len > 0 && time > 0 && _hasPlayed)
        {
            _lastPosition = _player.Position;
            _lastTimeMs = time;
        }
        if (!_seeking && len > 0) Seek.Value = _lastPosition;
        // Katselukohta talteen 15 s välein
        if (++_ticks % 30 == 0 && _player.IsPlaying) SaveProgress();
    }

    private void TogglePause() => Task.Run(() => _player.Pause());

    private void SeekBy(int seconds)
    {
        var len = _player.Length;
        if (len <= 0 || !_player.IsSeekable) return;
        SeekTo(Math.Clamp(_player.Time + seconds * 1000L, 0, len - 1000));
    }

    private void SeekTo(long ms)
    {
        if (!_player.IsSeekable) return;
        Task.Run(() => _player.Time = ms);
    }

    private void OnSeekStart(object sender, RoutedEventArgs e) => _seeking = true;

    private void OnSeekEnd(object sender, RoutedEventArgs e)
    {
        _seeking = false;
        var pos = (float)Seek.Value;
        Task.Run(() => _player.Position = pos);
    }

    private void OnSeekClick(object sender, MouseButtonEventArgs e)
    {
        if (_seeking) return;
        var pos = (float)Seek.Value;
        Task.Run(() => _player.Position = pos);
    }

    private void OnPlayPause(object sender, RoutedEventArgs e) => TogglePause();
    private void OnBack10(object sender, RoutedEventArgs e) => SeekBy(-10);
    private void OnFwd30(object sender, RoutedEventArgs e) => SeekBy(30);
    private void OnNextClick(object sender, RoutedEventArgs e) => PlayNext();
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnOverlayDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) Close();
        else if (e.OriginalSource == Overlay) { TogglePause(); ShowControls(); }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Overlay);
        if (Math.Abs(p.X - _lastMouse.X) > 2 || Math.Abs(p.Y - _lastMouse.Y) > 2) ShowControls();
        _lastMouse = p;
    }

    private void ShowControls()
    {
        TopBar.Visibility = BottomBar.Visibility = Visibility.Visible;
        Overlay.Cursor = null;
        _hide.Stop();
        _hide.Start();
    }

    private void HideControls()
    {
        _hide.Stop();
        if (BottomBar.IsMouseOver || TopBar.IsMouseOver || Controls.IsMenuOpen || NextCard.IsVisible) { _hide.Start(); return; }
        TopBar.Visibility = BottomBar.Visibility = Visibility.Collapsed;
        Overlay.Cursor = Cursors.None;
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveProgress();
        _tick.Stop();
        _nextTimer.Stop();
        _playback.Dispose();
        _hide.Stop();
        try { _settings.Save(); } catch { }
        Video.MediaPlayer = null;
        _service.Release(_player);
        base.OnClosed(e);
    }
}
