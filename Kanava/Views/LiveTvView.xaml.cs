using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kanava.Models;
using Kanava.ViewModels;

namespace Kanava.Views;

public partial class LiveTvView : UserControl
{
    private readonly DispatcherTimer _hideBar = new() { Interval = TimeSpan.FromSeconds(3) };
    private LiveTvViewModel? _vm;

    public LiveTvView()
    {
        InitializeComponent();
        _hideBar.Tick += (_, _) =>
        {
            // Ei piiloteta, jos hiiri on palkin päällä tai raita-/tekstitysvalikko on auki
            if (FullscreenControls.IsMenuOpen || FullscreenBar.IsMouseOver) return;
            _hideBar.Stop();
            FullscreenBar.Visibility = Visibility.Collapsed;
            Overlay.Cursor = Cursors.None;
        };
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) =>
        {
            AttachPlayer();
            // Overlay-ikkuna luodaan VideoView:n omassa Loaded-vaiheessa – synkronoidaan sen jälkeen
            Dispatcher.BeginInvoke(SyncOverlay, DispatcherPriority.ContextIdle);
            if (Window.GetWindow(this) is { } host)
                host.StateChanged += (_, _) => Dispatcher.BeginInvoke(SyncOverlay, DispatcherPriority.ContextIdle);
        };
        IsVisibleChanged += (_, _) => SyncOverlay();

        // Raahausjärjestely (vain järjestystilassa)
        Controls.ReorderBehavior.Attach(Cards,
            item => _vm is { IsReorderMode: true, CanReorderChannels: true } && item is LiveChannel,
            (item, target) => _vm?.MoveChannel((LiveChannel)item, target as LiveChannel));
        CategoryOrderUi.Attach(CategoryList, () => _vm?.CategoryOrder);

        // TV-opas: jälkikatselu ja muistutukset
        Guide.IsReminded = (ch, p) => _vm?.Session.UserData.IsReminded(ch.StreamId, p.Start) == true;
        Guide.IsRecorded = (ch, p) => Main?.Recordings?.IsScheduled(ch.StreamId, p.Start) == true;
        Guide.ProgramMenuRequested += OnProgramMenu;
        Guide.ProgramActivated = (ch, p) =>
        {
            // Tuplaklikkaus päättyneeseen arkisto-ohjelmaan → jälkikatselu
            if (p.End > DateTime.Now || !Controls.EpgGrid.CatchupAvailable(ch, p, DateTime.Now)) return false;
            PlayCatchup(ch, p);
            return true;
        };
    }

    /// <summary>
    /// VideoView:n overlay on erillinen läpinäkyvä ikkuna, joka ei seuraa WPF:n näkyvyyttä.
    /// Piilotetaan se itse, kun Live TV ei näy – muuten se peittää ja nappaa klikkaukset
    /// muista näkymistä (esim. kirjautumislomakkeelta).
    /// </summary>
    private void SyncOverlay()
    {
        var host = Window.GetWindow(this);
        if (host is null) return;
        var active = IsVisible;
        foreach (Window w in Application.Current.Windows)
        {
            // LibVLCSharp.WPF:n ForegroundWindow; omistaja asetetaan vasta sen omassa Loaded-käsittelijässä
            if (w == host || w.Title != "LibVLCSharp.WPF" || (w.Owner is not null && w.Owner != host)) continue;
            w.Opacity = active ? 1 : 0;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) continue;
            var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            ex = active ? ex & ~WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT;
            SetWindowLong(hwnd, GWL_EXSTYLE, ex);        }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);

    private void PlayCatchup(LiveChannel ch, EpgProgram p)
    {
        if (_vm is null || Window.GetWindow(this)?.DataContext is not MainViewModel main) return;
        main.PlayVod(_vm.CatchupRequest(ch, p));
    }

    private void OnProgramMenu(LiveChannel ch, EpgProgram p)
    {
        if (_vm is not { } vm) return;
        var now = DateTime.Now;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = $"{p.Title}  ·  {p.Start:ddd d.M. HH.mm}", IsEnabled = false, FontWeight = FontWeights.SemiBold });

        MenuItem Item(string header, string glyph, Action action)
        {
            var mi = new MenuItem { Header = header, Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13 } };
            mi.Click += (_, _) => action();
            return mi;
        }

        if (Controls.EpgGrid.CatchupAvailable(ch, p, now))
            menu.Items.Add(Item(p.End > now ? "Katso alusta (jälkikatselu)" : "Katso jälkikatseluna", "", () => PlayCatchup(ch, p)));
        else if (p.End <= now)
            menu.Items.Add(new MenuItem { Header = ch.HasArchive ? "Ohjelma ei ole enää arkistossa" : "Kanavalla ei ole jälkikatselua", IsEnabled = false });

        if (p.IsNow(now) || p.Start > now)
            menu.Items.Add(Item("Katso kanavaa nyt", "", () => vm.SelectAndPlay(ch)));

        if (p.End > now && Main?.Recordings is { } recs)
        {
            var scheduled = recs.IsScheduled(ch.StreamId, p.Start);
            menu.Items.Add(Item(scheduled ? "Peru tallennus" : "Tallenna ohjelma", "\uE7C8", () =>
            {
                var set = recs.ToggleSchedule(ch, p);
                vm.Playback.ShowOsd(set ? $"Tallennus ajastettu: {p.Title} {p.Start:HH.mm}" : "Tallennus peruttu");
                vm.RefreshRecording();
                Guide.InvalidateGuide();
            }));
        }

        if (p.Start > now)
        {
            var on = vm.Session.UserData.IsReminded(ch.StreamId, p.Start);
            menu.Items.Add(Item(on ? "Peru muistutus" : "Muistuta 5 min ennen alkua", "", () =>
            {
                var set = vm.Session.UserData.ToggleReminder(ch, p);
                vm.Playback.ShowOsd(set ? $"Muistutus: {p.Title} {p.Start:HH.mm}" : "Muistutus peruttu");
                Guide.InvalidateGuide();
            }));
        }
        menu.PlacementTarget = Guide;
        menu.IsOpen = true;
    }

    private MainViewModel? Main => Window.GetWindow(this)?.DataContext as MainViewModel;

    private void OnMultiview(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.OpenMultiview();

    private void OnRecord(object sender, RoutedEventArgs e)
    {
        if (_vm?.PlayingChannel is not { } ch || Main?.Recordings is not { } recs) return;
        if (recs.ActiveFor(ch.StreamId) is { } active)
        {
            recs.Cancel(active.Id);
            _vm.Playback.ShowOsd("Tallennus lopetettu – tiedosto: " + System.IO.Path.GetFileName(active.File));
        }
        else
        {
            var r = recs.RecordNow(ch);
            _vm.Playback.ShowOsd(r.Status == "Failed" ? "Tallennus epäonnistui: " + r.Error : $"Tallennetaan: {ch.Name}");
        }
        _vm.RefreshRecording();
    }

    private void OnPip(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.OpenPipManually();

    private void AttachPlayer()
    {
        if (_vm is not null && Video.MediaPlayer is null)
            Video.MediaPlayer = _vm.Session.Player.Preview;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Playback.PropertyChanged -= OnPlaybackChanged;
        }
        _vm = e.NewValue as LiveTvViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.Playback.PropertyChanged += OnPlaybackChanged;
            if (IsLoaded) AttachPlayer();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveTvViewModel.IsFullscreen))
        {
            if (_vm!.IsFullscreen) ShowBarBriefly();
            else
            {
                _hideBar.Stop();
                FullscreenBar.Visibility = Visibility.Collapsed;
                Overlay.Cursor = null;
            }
        }
    }

    /// <summary>Äänenvoimakkuuden tai raidan muutos näyttää palkin hetkeksi (kuten TV:n OSD).</summary>
    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm?.IsFullscreen == true && e.PropertyName is nameof(PlaybackViewModel.Volume) or nameof(PlaybackViewModel.IsMuted)
                or nameof(PlaybackViewModel.SelectedAudioId) or nameof(PlaybackViewModel.SelectedSubtitleId))
            ShowBarBriefly();
    }

    private void ShowBarBriefly()
    {
        FullscreenBar.Visibility = Visibility.Visible;
        Overlay.Cursor = null;
        _hideBar.Stop();
        _hideBar.Start();
    }

    private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) _vm?.ToggleFullscreenCommand.Execute(null);
    }

    private Point _lastMouse;
    private void OnOverlayMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Overlay);
        if (_vm?.IsFullscreen == true && (Math.Abs(p.X - _lastMouse.X) > 2 || Math.Abs(p.Y - _lastMouse.Y) > 2))
            ShowBarBriefly();
        _lastMouse = p;
    }

    // ---------- Oikean painikkeen valikot ----------

    private static MenuItem Item(string header, string icon, Action action, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, Tag = icon, IsEnabled = enabled };
        mi.Click += (_, _) => action();
        return mi;
    }

    private void OnCardMenu(object sender, ContextMenuEventArgs e)
    {
        var menu = Cards.ContextMenu!;
        menu.Items.Clear();
        if (_vm is null || FindParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not LiveChannel ch)
        {
            e.Handled = true;
            return;
        }
        var vm = _vm;
        menu.Items.Add(Item("Toista", "", () => { vm.SelectedChannel = ch; vm.Play(ch); }));
        menu.Items.Add(Item("Toista koko näytössä", "", () => vm.PlayFullscreenCommand.Execute(ch)));
        menu.Items.Add(Item(ch.IsFavorite ? "Poista suosikeista" : "Lisää suosikkeihin", ch.IsFavorite ? "" : "",
            () => vm.ToggleFavoriteCommand.Execute(ch)));
        menu.Items.Add(new Separator());
        var can = vm.CanReorderChannels;
        menu.Items.Add(Item("Siirrä alkuun", "", () => vm.MoveChannelToTopCommand.Execute(ch), can));
        menu.Items.Add(Item("Siirrä loppuun", "", () => vm.MoveChannelToBottomCommand.Execute(ch), can));
        menu.Items.Add(Item("Palauta tämän listan oletusjärjestys", "", () => vm.ResetChannelOrderCommand.Execute(null), can));
        menu.Items.Add(Item(vm.IsReorderMode ? "Lopeta järjestäminen" : "Järjestä raahaamalla…", "", () => vm.ToggleReorderCommand.Execute(null)));
    }

    private void OnGuideNow(object sender, RoutedEventArgs e) => Guide.ScrollToNow();

    private void OnManageChannels(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is MainViewModel main) main.Navigate(Section.ChannelManager);
    }

    private void OnCardDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Vain jos tuplaklikattiin korttia (ei vierityspalkkia tai sydäntä)
        var item = FindParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is LiveChannel ch && FindParent<Button>(e.OriginalSource as DependencyObject) is null)
            _vm?.PlayFullscreenCommand.Execute(ch);
    }

    internal static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}
