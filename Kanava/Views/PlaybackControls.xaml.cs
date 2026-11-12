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
