using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kanava.Models;
using Kanava.Services;
using LibVLCSharp.WPF;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Kanava.Views;

/// <summary>
/// Monikatselu: neljä kanavaa yhtä aikaa (2×2). Jokaisella ruudulla oma soitin ja yhteys;
/// ääni kuuluu vain valitusta ruudusta.
/// </summary>
public partial class MultiviewWindow : Window
{
    private sealed class Tile
    {
        public required Border Frame;
        public required VideoView View;
        public required MediaPlayer Player;
        public required TextBlock Label;
        public required TextBlock Empty;
        public required Button CloseButton;
        public required Grid Overlay;
        public LiveChannel? Channel;
    }

    private readonly AppSession _session;
    private readonly List<Tile> _tiles = [];
    private readonly List<LiveChannel> _channels;
    private int _active;
    private bool _fullscreen;
    private WindowState _restoreState;

    public MultiviewWindow(AppSession session, LiveChannel? first)
    {
        InitializeComponent();
        _session = session;
        _channels = session.VisibleLiveChannels.ToList();
        ChannelList.ItemsSource = _channels;
        for (var i = 0; i < 4; i++) _tiles.Add(CreateTile(i));
        SetActive(0);
        UpdateStatus();
        Loaded += (_, _) => { if (first is not null) Assign(0, first); };
        PreviewKeyDown += OnKey;
    }

    private int MaxConnections => _session.Account.MaxConnections;

    private Tile CreateTile(int index)
    {
        var player = _session.Player.CreatePlayer();
        var label = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(10, 8, 40, 0), VerticalAlignment = VerticalAlignment.Top, TextTrimming = TextTrimming.CharacterEllipsis };
        var empty = new TextBlock { Text = "Tyhjä ruutu\nKaksoisklikkaa kanavaa listasta", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button { Style = (Style)FindResource("IconButton"), Content = "", Foreground = Brushes.White, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4), Visibility = Visibility.Collapsed };
        AutomationPropertiesHelper(close, $"Tyhjennä ruutu {index + 1}");
        close.Click += (_, _) => Clear(index);

        // Overlay (VideoView:n sisältö piirtyy omaan ikkunaansa): klikkaus valitsee ruudun
        var overlay = new Grid { Background = EmptyBg };
        overlay.Children.Add(empty);
        var labelBg = new Border { Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6), Child = label };
        overlay.Children.Add(labelBg);
        overlay.Children.Add(close);
        overlay.MouseLeftButtonDown += (_, e) =>
        {
            SetActive(index);
            if (e.ClickCount == 2) ToggleFullscreen();
        };

        var view = new VideoView { Background = Brushes.Black, Content = overlay };
        var frame = new Border { BorderThickness = new Thickness(2), Margin = new Thickness(3), Child = view, CornerRadius = new CornerRadius(4) };
        Tiles.Children.Add(frame);
        view.Loaded += (_, _) => view.MediaPlayer ??= player;
        player.Playing += (_, _) => Dispatcher.BeginInvoke(() => ApplyAudio());
        return new Tile { Frame = frame, View = view, Player = player, Label = label, Empty = empty, CloseButton = close, Overlay = overlay };
    }

    // Tyhjä ruutu peitetään (VLC:n ikkuna on valkoinen ennen toistoa); toistossa lähes läpinäkyvä, jotta klikkaukset osuvat
    private static readonly Brush EmptyBg = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
    private static readonly Brush PlayingBg = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

    private static void AutomationPropertiesHelper(DependencyObject d, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(d, name);

    private void SetActive(int index)
    {
        _active = index;
        for (var i = 0; i < _tiles.Count; i++)
            _tiles[i].Frame.BorderBrush = i == index ? (Brush)FindResource("AccentBrush") : new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26));
        ApplyAudio();
    }

    /// <summary>Ääni vain valitusta ruudusta.</summary>
    private void ApplyAudio()
    {
        var volume = _session.Settings.Settings.Volume;
        for (var i = 0; i < _tiles.Count; i++)
        {
            var t = _tiles[i];
            var mute = i != _active;
            Task.Run(() => { try { t.Player.Mute = mute; if (!mute) t.Player.Volume = volume; } catch { } });
        }
    }

    private int UsedSlots => _tiles.Count(t => t.Channel is not null);

    private void Assign(int index, LiveChannel ch)
    {
        var tile = _tiles[index];
        if (tile.Channel is null && MaxConnections > 0 && UsedSlots >= MaxConnections)
        {
            StatusText.Text = $"Tilauksesi sallii {MaxConnections} yhtäaikaista yhteyttä – tyhjennä ensin jokin ruutu.";
            return;
        }
        tile.Channel = ch;
        tile.Label.Text = ch.Name;
        tile.Empty.Visibility = Visibility.Collapsed;
        tile.Overlay.Background = PlayingBg;
        tile.CloseButton.Visibility = Visibility.Visible;
        _session.Player.Play(tile.Player, _session.Client.LiveUrl(ch.StreamId, _session.Settings.Settings.LiveFormat));
        SetActive(index);
        UpdateStatus();
    }

    private void Clear(int index)
    {
        var tile = _tiles[index];
        tile.Channel = null;
        tile.Label.Text = "";
        tile.Empty.Visibility = Visibility.Visible;
        tile.Overlay.Background = EmptyBg;
        tile.CloseButton.Visibility = Visibility.Collapsed;
        _session.Player.Stop(tile.Player);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var max = MaxConnections > 0 ? $" / {MaxConnections} sallittua yhteyttä" : "";
        StatusText.Text = $"{UsedSlots} kanavaa käynnissä{max}.";
    }

    /// <summary>Valittu ruutu, jos tyhjä; muuten ensimmäinen tyhjä; muuten valittu (korvataan).</summary>
    private int TargetSlot()
    {
        if (_tiles[_active].Channel is null) return _active;
        var empty = _tiles.FindIndex(t => t.Channel is null);
        return empty >= 0 ? empty : _active;
    }

    private void OnChannelDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChannelList.SelectedItem is LiveChannel ch) Assign(TargetSlot(), ch);
    }

    private void OnChannelKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ChannelList.SelectedItem is LiveChannel ch) { Assign(TargetSlot(), ch); e.Handled = true; }
    }

    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        var q = Search.Text.Trim();
        ChannelList.ItemsSource = q.Length == 0 ? _channels : _channels.Where(c => c.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || c.NumberText == q).ToList();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 || (e.Key == Key.Escape && _fullscreen)) { ToggleFullscreen(); e.Handled = true; }
        else if (e.Key == Key.Delete && Keyboard.FocusedElement is not TextBox) { Clear(_active); e.Handled = true; }
        else if (e.Key is >= Key.D1 and <= Key.D4 && Keyboard.FocusedElement is not TextBox) { SetActive(e.Key - Key.D1); e.Handled = true; }
    }

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            _restoreState = WindowState;
            Side.Visibility = Visibility.Collapsed;
            SideColumn.Width = new GridLength(0);
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            Side.Visibility = Visibility.Visible;
            SideColumn.Width = new GridLength(260);
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _restoreState;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var t in _tiles)
        {
            t.View.MediaPlayer = null;
            _session.Player.Release(t.Player);
        }
        base.OnClosed(e);
    }
}
