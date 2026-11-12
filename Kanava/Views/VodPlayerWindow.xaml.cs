using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Kanava.Services;
using Kanava.ViewModels;
using LibVLCSharp.Shared;

namespace Kanava.Views;

/// <summary>Elokuvien ja sarjojen jaksojen toisto koko näytössä.</summary>
public partial class VodPlayerWindow : Window
{
    private readonly PlayerService _service;
    private readonly SettingsStore _settings;
    private readonly MediaPlayer _player;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly PlaybackViewModel _playback;
    private bool _seeking;
    private float _lastPosition;
    private Point _lastMouse;

    public VodPlayerWindow(PlayerService service, SettingsStore settings, string url, string title)
    {
        InitializeComponent();
        _service = service;
        _settings = settings;
        _player = service.CreatePlayer();
        TitleText.Text = title;
        Title = title + " – Töllö";

        _player.Playing += (_, _) => Dispatcher.BeginInvoke(() => { StatusText.Visibility = Visibility.Collapsed; PlayPause.Content = ""; });
        _player.Paused += (_, _) => Dispatcher.BeginInvoke(() => PlayPause.Content = "");
        _player.Buffering += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = $"Puskuroidaan… {e.Cache:0} %";
            StatusText.Visibility = e.Cache < 100 ? Visibility.Visible : Visibility.Collapsed;
        });
        _player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() => { StatusText.Text = "Toisto epäonnistui."; StatusText.Visibility = Visibility.Visible; ShowControls(); });
        _player.EndReached += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            // Suljetaan vain, jos video todella katsottiin loppuun – muuten näytetään virhe
            if (_lastPosition > 0.97f) { Close(); return; }
            StatusText.Text = _lastPosition > 0 ? "Yhteys katkesi." : "Toisto epäonnistui – tiedostoa ei voitu avata.";
            StatusText.Visibility = Visibility.Visible;
            PlayPause.Content = "";
            ShowControls();
        });

        _tick.Tick += (_, _) => UpdatePosition();
        _hide.Tick += (_, _) => HideControls();

        _playback = new PlaybackViewModel(_player, service.LibVLC, settings);
        Overlay.DataContext = _playback;
        Loaded += (_, _) =>
        {
            Video.MediaPlayer = _player;
            _player.Volume = _playback.Volume;
            _service.Play(_player, url);
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
        LenText.Text = len > 0 ? Fmt(len) : "--:--";
        PosText.Text = Fmt(_player.Time);
        if (len > 0) _lastPosition = _player.Position;
        if (!_seeking && len > 0) Seek.Value = _lastPosition;
    }

    private void TogglePause() => Task.Run(() => _player.Pause());

    private void SeekBy(int seconds)
    {
        var len = _player.Length;
        if (len <= 0 || !_player.IsSeekable) return;
        var target = Math.Clamp(_player.Time + seconds * 1000L, 0, len - 1000);
        Task.Run(() => _player.Time = target);
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
        if (BottomBar.IsMouseOver || TopBar.IsMouseOver || Controls.IsMenuOpen) { _hide.Start(); return; }
        TopBar.Visibility = BottomBar.Visibility = Visibility.Collapsed;
        Overlay.Cursor = Cursors.None;
    }

    protected override void OnClosed(EventArgs e)
    {
        _tick.Stop();
        _playback.Dispose();
        _hide.Stop();
        try { _settings.Save(); } catch { }
        var p = _player;
        Video.MediaPlayer = null;
        // Pysäytys ja vapautus taustalla, ettei UI jäädy
        Task.Run(() => { p.Stop(); p.Dispose(); });
        base.OnClosed(e);
    }
}
