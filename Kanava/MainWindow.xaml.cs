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
            if (_vm is not null) { _vm.PropertyChanged -= OnMainChanged; _vm.PlayVodRequested -= OnPlayVod; }
            _vm = e.NewValue as MainViewModel;
            if (_vm is not null) { _vm.PropertyChanged += OnMainChanged; _vm.PlayVodRequested += OnPlayVod; }
        };
        // Näppäimet kaikista sovelluksen ikkunoista (myös VideoView:n overlay-ikkunasta)
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnAnyKeyDown));
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
            var name = _vm!.Session?.Account.Username ?? "";
            AvatarText.Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
        }
    }

    private void OnPlayVod(string url, string title)
    {
        var vm = _vm!;
        var win = new Views.VodPlayerWindow(vm.Player, vm.Settings, url, title) { Owner = this };
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

    private void OnAnyKeyDown(object sender, KeyEventArgs e)
    {
        // Vain tämä ikkuna tai sen omistama overlay-ikkuna (ei esim. VOD-soitin)
        if (sender is not Window w || (w != this && w.Owner != this)) return;
        if (_live is null || _vm?.CurrentSection != Section.LiveTv) return;
        if (e.Key == Key.Escape && _live.IsFullscreen)
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
            else if (_live.IsFullscreen && e.Key == Key.Up) { _live.Playback.VolumeUpCommand.Execute(null); e.Handled = true; }
            else if (_live.IsFullscreen && e.Key == Key.Down) { _live.Playback.VolumeDownCommand.Execute(null); e.Handled = true; }
        }
    }
}
