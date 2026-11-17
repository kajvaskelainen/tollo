using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Kanava.ViewModels;

namespace Kanava.Views;

/// <summary>Äänenvoimakkuus, ääniraita- ja tekstitysvalinta. DataContext = PlaybackViewModel.</summary>
public partial class PlaybackControls : UserControl
{
    public static readonly DependencyProperty SliderWidthProperty = DependencyProperty.Register(
        nameof(SliderWidth), typeof(double), typeof(PlaybackControls), new PropertyMetadata(100d));

    public double SliderWidth { get => (double)GetValue(SliderWidthProperty); set => SetValue(SliderWidthProperty, value); }

    /// <summary>Onko jokin valikko auki (koko näytön palkki ei saa piiloutua silloin).</summary>
    public bool IsMenuOpen { get; private set; }

    private PlaybackViewModel? _vm;

    public PlaybackControls()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = e.NewValue as PlaybackViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
            UpdateLabels();
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackViewModel.AudioTracks) or nameof(PlaybackViewModel.SelectedAudioId)
            or nameof(PlaybackViewModel.SubtitleTracks) or nameof(PlaybackViewModel.SelectedSubtitleId))
            UpdateLabels();
    }

    private void UpdateLabels()
    {
        if (_vm is null) return;
        var audio = _vm.AudioTracks.FirstOrDefault(t => t.Id == _vm.SelectedAudioId);
        AudioLabel.Text = audio is null ? "Ääni" : Short(audio.Name);
        AudioButton.Opacity = _vm.AudioTracks.Count > 1 ? 1 : 0.6;
        var sub = _vm.SubtitleTracks.FirstOrDefault(t => t.Id == _vm.SelectedSubtitleId && t.Id >= 0);
        SubLabel.Text = sub is null ? "Ei tekstitystä" : Short(sub.Name);
        SubButton.Opacity = _vm.SubtitleTracks.Count > 1 ? 1 : 0.6;
    }

    private static string Short(string s) => s.Length > 16 ? s[..15] + "…" : s;

    private void OnVolumeWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm is null) return;
        _vm.Volume = Math.Clamp(_vm.Volume + (e.Delta > 0 ? 5 : -5), 0, 100);
        e.Handled = true;
    }

    private void OnAudioMenu(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var vm = _vm;
        var items = vm.AudioTracks.Select(t => (t.Name, t.Id == vm.SelectedAudioId, (Action)(() => vm.SelectAudio(t.Id)))).ToList();
        ShowMenu(AudioButton, "Ääniraita", items, vm.AudioTracks.Count == 0 ? "Ääniraitoja ei löytynyt" : null);
    }

    private void OnSubtitleMenu(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var vm = _vm;
        var items = vm.SubtitleTracks.Select(t => (t.Name, t.Id == vm.SelectedSubtitleId, (Action)(() => vm.SelectSubtitle(t.Id)))).ToList();
        ShowMenu(SubButton, "Tekstitys", items, vm.SubtitleTracks.Count <= 1 ? "Lähetyksessä ei ole tekstityksiä" : null);
    }

    private void OnMoreMenu(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var vm = _vm;
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = PlacementMode.Top };

        MenuItem Item(string header, Action action, bool? check = null, string? gesture = null)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            if (check is { } c) { mi.IsCheckable = true; mi.IsChecked = c; }
            mi.Click += (_, _) => action();
            return mi;
        }

        // Äänen synkronointi
        var sync = new MenuItem { Header = $"Äänen synkronointi ({PlaybackViewModel.DelayText(vm.AudioDelayMs)})" };
        sync.Items.Add(new MenuItem { Header = "Ääni on kuvaa edellä → viivästä ääntä", IsEnabled = false });
        sync.Items.Add(Item("Viivästä ääntä 0,1 s", () => vm.ShiftAudio(100), gesture: "K"));
        sync.Items.Add(Item("Viivästä ääntä 0,5 s", () => vm.ShiftAudio(500)));
        sync.Items.Add(new Separator());
        sync.Items.Add(new MenuItem { Header = "Ääni on kuvaa perässä → aikaista ääntä", IsEnabled = false });
        sync.Items.Add(Item("Aikaista ääntä 0,1 s", () => vm.ShiftAudio(-100), gesture: "J"));
        sync.Items.Add(Item("Aikaista ääntä 0,5 s", () => vm.ShiftAudio(-500)));
        sync.Items.Add(new Separator());
        sync.Items.Add(Item("Nollaa", () => vm.ResetAudioDelayCommand.Execute(null)));
        sync.Items.Add(new MenuItem { Header = "Asetus muistetaan kanavakohtaisesti", IsEnabled = false });
        menu.Items.Add(sync);

        // Kuvasuhde ja zoom
        var aspect = new MenuItem { Header = "Kuvasuhde ja zoom" };
        foreach (var (label, a) in new[] { ("Automaattinen", ""), ("16:9", "16:9"), ("4:3", "4:3"), ("16:10", "16:10"), ("21:9", "21:9") })
            aspect.Items.Add(Item(label, () => vm.SetAspect(a, ""), check: vm.Crop.Length == 0 && vm.Aspect == a));
        aspect.Items.Add(new Separator());
        aspect.Items.Add(Item("Zoomaa: rajaa 16:9 (poistaa mustat reunat)", () => vm.SetAspect("", "16:9"), check: vm.Crop == "16:9"));
        aspect.Items.Add(Item("Zoomaa: rajaa 4:3", () => vm.SetAspect("", "4:3"), check: vm.Crop == "4:3"));
        menu.Items.Add(aspect);

        menu.Items.Add(Item("Äänen tasoitus (mainokset eivät huuda)", vm.ToggleNormalize, check: vm.NormalizeAudio));

        // Uniajastin
        var sleep = new MenuItem { Header = Services.SleepTimer.IsActive ? $"Uniajastin ({Services.SleepTimer.RemainingText} jäljellä)" : "Uniajastin" };
        foreach (var m in new[] { 15, 30, 45, 60, 90, 120 })
            sleep.Items.Add(Item(m >= 60 ? $"{m / 60} h {(m % 60 > 0 ? $"{m % 60} min" : "")}".Trim() : $"{m} min", () =>
            {
                Services.SleepTimer.Start(m);
                vm.ShowOsd($"Uniajastin: toisto pysähtyy {m} min kuluttua");
            }));
        if (Services.SleepTimer.IsActive)
        {
            sleep.Items.Add(new Separator());
            sleep.Items.Add(Item("Peru uniajastin", () => { Services.SleepTimer.Cancel(); vm.ShowOsd("Uniajastin peruttu"); }));
        }
        menu.Items.Add(sleep);

        // Suoratoisto (Chromecast)
        Services.CastDiscovery.Ensure(vm.LibVLC);
        var cast = new MenuItem { Header = vm.CastTarget is { } target ? $"Suoratoisto: {target}" : "Suoratoista televisioon (Chromecast)" };
        var devices = Services.CastDiscovery.Devices;
        foreach (var d in devices)
            cast.Items.Add(Item(d.Name, () => vm.CastTo(d), check: vm.CastTarget == d.Name));
        if (devices.Count == 0)
            cast.Items.Add(new MenuItem { Header = "Etsitään laitteita… (avaa valikko uudelleen hetken päästä)", IsEnabled = false });
        if (vm.CastTarget is not null)
        {
            cast.Items.Add(new Separator());
            cast.Items.Add(Item("Lopeta suoratoisto", () => vm.CastTo(null)));
        }
        menu.Items.Add(cast);

        menu.Opened += (_, _) => IsMenuOpen = true;
        menu.Closed += (_, _) => IsMenuOpen = false;
        menu.IsOpen = true;
    }

    private void ShowMenu(FrameworkElement target, string title, List<(string name, bool selected, Action action)> items, string? note)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Top };
        menu.Items.Add(new MenuItem { Header = title, IsEnabled = false, FontWeight = FontWeights.SemiBold });
        foreach (var (name, selected, action) in items)
        {
            var mi = new MenuItem { Header = name, IsCheckable = true, IsChecked = selected };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        if (note is not null) menu.Items.Add(new MenuItem { Header = note, IsEnabled = false });
        menu.Opened += (_, _) => IsMenuOpen = true;
        menu.Closed += (_, _) => IsMenuOpen = false;
        menu.IsOpen = true;
    }
}
