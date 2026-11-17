using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Kanava.ViewModels;

namespace Kanava;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private LiveTvViewModel? _live;
    private WindowState _restoreState;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) { _vm.PropertyChanged -= OnMainChanged; _vm.PlayVodRequested -= OnPlayVod; _vm.ReminderDue -= OnReminderDue; }
            _vm = e.NewValue as MainViewModel;
            if (_vm is not null) { _vm.PropertyChanged += OnMainChanged; _vm.PlayVodRequested += OnPlayVod; _vm.ReminderDue += OnReminderDue; }
        };
        // Näppäimet kaikista sovelluksen ikkunoista (myös VideoView:n overlay-ikkunasta)
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnAnyKeyDown));
        StateChanged += OnWindowStateChanged;
        Closing += OnWindowClosing;
        Closed += (_, _) => ClosePip(resume: false);
        Services.SleepTimer.Expired += OnSleepTimerExpired;
    }

    private void OnReminderDue(Services.Reminder r)
    {
        var toast = new Views.ReminderToast(r);
        toast.WatchRequested += () =>
        {
            // Pienoissoittimessa vaihdetaan kanava siellä, muuten tuodaan Töllö esiin
            if (_pip is null)
            {
                if (!IsVisible) { Show(); SetOverlaysVisible(true); }
                if (WindowState == WindowState.Minimized) WindowState = _stateBeforeMinimize;
                Activate();
            }
            _vm?.WatchReminder(r);
        };
        toast.Show();
    }

    /// <summary>Uniajastin päättyi: pysäytetään kaikki toisto (live, pienoissoitin, elokuvat).</summary>
    private void OnSleepTimerExpired()
    {
        foreach (var w in OwnedWindows.OfType<Views.VodPlayerWindow>().ToList()) w.Close();
        var hidden = !IsVisible && _pip is not null; // suljettu pienoissoittimeen → ohjelma sulkeutuu
        ClosePip(resume: false);
        _live?.StopPlayback();
        if (hidden) { _exiting = true; Close(); }
    }

    // ---------- Monikatselu ----------

    private Views.MultiviewWindow? _multiview;

    /// <summary>Avaa monikatselun; esikatselu pysäytetään (yhteyksien säästämiseksi) ja jatkuu suljettaessa.</summary>
    public void OpenMultiview()
    {
        if (_multiview is not null) { _multiview.Activate(); return; }
        if (_vm?.Session is not { } session || _live is null) return;
        var first = _live.PlayingChannel;
        _live.Suspend();
        _multiview = new Views.MultiviewWindow(session, first);
        _multiview.Closed += (_, _) =>
        {
            _multiview = null;
            if (_vm?.CurrentSection == Section.LiveTv && WindowState != WindowState.Minimized && _pip is null) _live?.Resume();
        };
        _multiview.Show();
    }

    // ---------- Pienoissoitin (PiP) ----------

    private Views.PipWindow? _pip;
    private bool _pipRequested;     // käyttäjä pyysi PiP:n napista / P-näppäimellä
    private bool _resumeOnRestore;  // PiP suljettiin pääikkunan ollessa pienennettynä
    private bool _exiting;
    private WindowState _stateBeforeMinimize = WindowState.Normal;

    private string PipMode => _vm?.Settings.Settings.PipMode ?? "minimize";

    private bool CanPip =>
        _pip is null && _vm is { IsLoggedIn: true, CurrentSection: Section.LiveTv } && _live?.PlayingChannel is not null
        && !OwnedWindows.OfType<Views.VodPlayerWindow>().Any() && _multiview is null;

    /// <summary>Live TV:n "Pienoissoitin"-nappi: pienentää ikkunan ja avaa PiP:n asetuksista riippumatta.</summary>
    public void OpenPipManually()
    {
        if (!CanPip) return;
        _pipRequested = true;
        WindowState = WindowState.Minimized;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            var wanted = _pipRequested || PipMode is "minimize" or "close";
            _pipRequested = false;
            if (wanted && CanPip) OpenPip();
            return;
        }
        _stateBeforeMinimize = WindowState;
        if (_pip is not null) ClosePip(resume: true);
        else if (_resumeOnRestore)
        {
            _resumeOnRestore = false;
            if (_vm?.CurrentSection == Section.LiveTv) _live?.Resume();
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // "Suljettaessa pienoissoittimeen": ikkuna piilotetaan ja toisto jatkuu PiP:ssä
        if (_exiting || PipMode != "close" || !CanPip) return;
        e.Cancel = true;
        Hide();
        SetOverlaysVisible(false);
        OpenPip();
    }

    private void OpenPip()
    {
        var vm = _vm!;
        _pip = new Views.PipWindow(vm.Player, vm.Settings, _live!, this);
        _pip.RestoreRequested += RestoreFromPip;
        _pip.Closed += OnPipClosedByUser;
        _pip.Show();
    }

    private void RestoreFromPip()
    {
        if (!IsVisible) { Show(); SetOverlaysVisible(true); }
        if (WindowState == WindowState.Minimized) WindowState = _stateBeforeMinimize; // → OnWindowStateChanged sulkee PiP:n
        if (_pip is not null) ClosePip(resume: true);
        Activate();
    }

    /// <summary>Sulkee PiP:n ohjelmallisesti; resume = jatketaanko toistoa pääikkunassa.</summary>
    private void ClosePip(bool resume)
    {
        if (_pip is not { } pip) return;
        _pip = null;
        pip.Closed -= OnPipClosedByUser;
        SyncVolumeFrom(pip);
        _live?.ExitPip(resume);
        pip.Close();
    }

    /// <summary>Käyttäjä sulki PiP:n: toisto loppuu. Jos pääikkuna oli suljettu, ohjelma sulkeutuu.</summary>
    private void OnPipClosedByUser(object? sender, EventArgs e)
    {
        var pip = _pip;
        _pip = null;
        if (pip is not null) SyncVolumeFrom(pip);
        _live?.ExitPip(resume: false);
        if (!IsVisible) { _exiting = true; Close(); }
        else _resumeOnRestore = true;
    }

    /// <summary>
    /// VideoView:n overlay on oma (omistettu) ikkunansa, joka ei piiloudu Hide()-kutsussa –
    /// piilotetaan ja palautetaan se pääikkunan mukana.
    /// </summary>
    private void SetOverlaysVisible(bool visible)
    {
        foreach (Window w in Application.Current.Windows)
            if (w != this && w.Owner == this && w.Title == "LibVLCSharp.WPF")
            {
                if (visible) w.Show(); else w.Hide();
            }
    }

    private void SyncVolumeFrom(Views.PipWindow pip)
    {
        if (_live is null || _vm is null) return;
        _live.Playback.Volume = _vm.Settings.Settings.Volume;
        _live.Playback.IsMuted = pip.IsMuted;
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.LiveTv))
        {
            if (_live is not null) _live.PropertyChanged -= OnLiveChanged;
            _live = _vm!.LiveTv;
            if (_live is not null) _live.PropertyChanged += OnLiveChanged;
            SetFullscreen(false);
        }
        else if (e.PropertyName == nameof(MainViewModel.Session))
        {
            if (_vm!.Session is null) _multiview?.Close();
            var name = _vm!.Session?.Account.Username ?? "";
            AvatarText.Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
        }
    }

    private void OnPlayVod(Models.PlaybackRequest request)
    {
        var vm = _vm!;
        var win = new Views.VodPlayerWindow(vm.Player, vm.Settings, vm.Session?.UserData, request) { Owner = this };
        win.Closed += (_, _) => { vm.OnVodClosed(); Activate(); };
        win.Show();
    }

    private void OnLiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveTvViewModel.IsFullscreen)) SetFullscreen(_live!.IsFullscreen);
    }

    private bool _isFullscreen;
    private void SetFullscreen(bool on)
    {        if (on == _isFullscreen) return;
        _isFullscreen = on;
        if (on)
        {
            _restoreState = WindowState;
            Sidebar.Visibility = Visibility.Collapsed;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // Normal → Maximized varmistaa, että tehtäväpalkki peittyy
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            Sidebar.Visibility = Visibility.Visible;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _restoreState;
        }
    }

    private static int? DigitOf(Key k) =>
        k is >= Key.D0 and <= Key.D9 ? k - Key.D0 : k is >= Key.NumPad0 and <= Key.NumPad9 ? k - Key.NumPad0 : null;

    private void OnAnyKeyDown(object sender, KeyEventArgs e)
    {
        // Vain tämä ikkuna tai sen omistama overlay-ikkuna (ei esim. VOD-soitin)
        if (sender is not Window w || (w != this && w.Owner != this)) return;
        if (_live is null || _vm?.CurrentSection != Section.LiveTv) return;
        if (e.Key == Key.P && Keyboard.FocusedElement is not System.Windows.Controls.TextBox && !_live.IsFullscreen)
        {
            OpenPipManually();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _live.IsFullscreen)
        {
            _live.IsFullscreen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.F11 || (e.Key == Key.F && Keyboard.FocusedElement is not System.Windows.Controls.TextBox))
        {
            _live.ToggleFullscreenCommand.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
        {
            // Äänenhallinta: M aina, nuolet vain koko näytössä (muuten ne selaavat listoja)
            if (e.Key == Key.M) { _live.Playback.ToggleMuteCommand.Execute(null); e.Handled = true; }
            else if (DigitOf(e.Key) is { } digit) { _live.TypeDigit(digit); e.Handled = true; }
            else if (e.Key == Key.Back) { _live.PreviousChannel(); e.Handled = true; }
            else if (e.Key == Key.PageUp) { _live.PlayAdjacent(-1); e.Handled = true; }
            else if (e.Key == Key.PageDown) { _live.PlayAdjacent(1); e.Handled = true; }
            else if (e.Key == Key.J) { _live.Playback.ShiftAudio(-100); e.Handled = true; }
            else if (e.Key == Key.K) { _live.Playback.ShiftAudio(100); e.Handled = true; }
            else if (_live.IsFullscreen && e.Key == Key.Up) { _live.Playback.VolumeUpCommand.Execute(null); e.Handled = true; }
            else if (_live.IsFullscreen && e.Key == Key.Down) { _live.Playback.VolumeDownCommand.Execute(null); e.Handled = true; }
        }
    }
}
