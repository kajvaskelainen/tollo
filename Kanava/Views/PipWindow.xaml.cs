using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Kanava.Services;
using Kanava.ViewModels;
using LibVLCSharp.Shared;

namespace Kanava.Views;

/// <summary>
/// Pienoissoitin: pieni, reunaton ja (valinnaisesti) aina päällimmäinen ikkuna näytön kulmassa.
/// Toistaa Live TV:n kanavaa omalla soittimellaan, kun pääikkuna on pienennetty tai suljettu.
/// Ikkunaa voi raahata; vapautettaessa se kiinnittyy lähimpään kulmaan, joka tallennetaan.
/// </summary>
public partial class PipWindow : Window
{
    private readonly PlayerService _service;
    private readonly SettingsStore _settings;
    private readonly LiveTvViewModel _live;
    private readonly MediaPlayer _player;
    private readonly PlaybackViewModel _playback;
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly IntPtr _monitorHint;

    /// <summary>Käyttäjä haluaa takaisin pääikkunaan (palautusnappi tai tuplaklikkaus).</summary>
    public event Action? RestoreRequested;

    public PipWindow(PlayerService service, SettingsStore settings, LiveTvViewModel live, Window main)
    {
        InitializeComponent();
        _service = service;
        _settings = settings;
        _live = live;
        _player = service.CreatePlayer();
        _playback = new PlaybackViewModel(_player, service.LibVLC, settings);
        // Avataan samalle näytölle, jolla pääikkuna oli
        _monitorHint = MonitorFromWindow(new WindowInteropHelper(main).Handle, MONITOR_DEFAULTTONEAREST);

        DataContext = live;
        MuteButton.DataContext = _playback;
        Topmost = settings.Settings.PipTopmost;
        Width = SizeFor(settings.Settings.PipSize);
        Height = Math.Round(Width * 9 / 16);

        _player.Playing += (_, _) => Dispatcher.BeginInvoke(() => StatusText.Visibility = Visibility.Collapsed);
        _player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() => ShowStatus("Toisto epäonnistui."));
        live.PropertyChanged += OnLiveChanged;
        _hide.Tick += (_, _) => { _hide.Stop(); if (!Overlay.IsMouseOver) Controls.Visibility = Visibility.Collapsed; };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        };
        Loaded += (_, _) =>
        {
            Place(_monitorHint, settings.Settings.PipCorner);
            Video.MediaPlayer = _player;
            _player.Volume = _playback.Volume;
            _live.EnterPip(_player);
            ShowControlsBriefly();
        };
    }

    public bool IsMuted => _playback.IsMuted;

    public static double SizeFor(string size) => size switch { "s" => 320, "l" => 560, _ => 420 };

    private void OnLiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveTvViewModel.PlayingChannel)) { ShowStatus("Ladataan…"); ShowControlsBriefly(); }
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    // ---------- Ohjaimet ----------

    private void ShowControlsBriefly()
    {
        Controls.Visibility = Visibility.Visible;
        _hide.Stop();
        _hide.Start();
    }

    private void OnOverlayEnter(object sender, MouseEventArgs e) { _hide.Stop(); Controls.Visibility = Visibility.Visible; }
    private void OnOverlayLeave(object sender, MouseEventArgs e) => ShowControlsBriefly();

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0) _playback.VolumeUpCommand.Execute(null); else _playback.VolumeDownCommand.Execute(null);
        e.Handled = true;
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e) => RestoreRequested?.Invoke();
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnPrevClick(object sender, RoutedEventArgs e) => _live.PlayAdjacent(-1);
    private void OnNextClick(object sender, RoutedEventArgs e) => _live.PlayAdjacent(1);

    private void OnOverlayDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement src || src.TemplatedParent is System.Windows.Controls.Button) return;
        if (e.ClickCount == 2) { RestoreRequested?.Invoke(); return; }
        // Raahaus: VLC:n overlay-ikkuna seuraa omistajaansa. Vapautettaessa kiinnitys lähimpään kulmaan.
        Mouse.Capture(null);
        try { DragMove(); } catch (InvalidOperationException) { return; }
        SnapToNearestCorner();
    }

    // ---------- Sijoittelu ----------

    private void SnapToNearestCorner()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(hwnd, out var r)) return;
        var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var work = WorkArea(mon);
        var cx = (r.Left + r.Right) / 2;
        var cy = (r.Top + r.Bottom) / 2;
        var corner = (cy < (work.Top + work.Bottom) / 2 ? "Top" : "Bottom") + (cx < (work.Left + work.Right) / 2 ? "Left" : "Right");
        _settings.Settings.PipCorner = corner;
        try { _settings.Save(); } catch { }
        Place(mon, corner);
    }

    /// <summary>Siirtää ikkunan näytön työalueen kulmaan (pikseleinä, toimii eri DPI-asetuksilla).</summary>
    private void Place(IntPtr monitor, string corner)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
        var work = WorkArea(monitor);
        var margin = (int)Math.Round(16 * VisualTreeHelperDpi());
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var x = corner.EndsWith("Left") ? work.Left + margin : work.Right - w - margin;
        var y = corner.StartsWith("Top") ? work.Top + margin : work.Bottom - h - margin;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private double VisualTreeHelperDpi() => System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;

    private static RECT WorkArea(IntPtr monitor)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)) return info.rcWork;
        var wa = SystemParameters.WorkArea;
        return new RECT { Left = (int)wa.Left, Top = (int)wa.Top, Right = (int)wa.Right, Bottom = (int)wa.Bottom };
    }

    protected override void OnClosed(EventArgs e)
    {
        _hide.Stop();
        _live.PropertyChanged -= OnLiveChanged;
        _playback.Dispose();
        try { _settings.Save(); } catch { }
        Video.MediaPlayer = null;
        _service.Release(_player);
        base.OnClosed(e);
    }

    // ---------- Win32 ----------

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
