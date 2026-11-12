using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kanava.Models;
using Kanava.Services;

namespace Kanava.Controls;

/// <summary>
/// TV-opasruudukko. Kanavasarake, aikajana ja ohjelmalohkot piirretään yhdellä pinnalla,
/// joten vaaka- ja pystyvieritys ovat aina synkronissa. Vain näkyvät rivit ja ohjelmat
/// piirretään, joten satoja/tuhansia kanavia voidaan selata kevyesti.
/// </summary>
public sealed class EpgGrid : Grid
{
    private readonly EpgSurface _surface = new();
    private readonly ScrollBar _v = new() { Orientation = Orientation.Vertical };
    private readonly ScrollBar _h = new() { Orientation = Orientation.Horizontal };

    public EpgGrid()
    {
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(_surface);
        SetColumn(_v, 1);
        SetRow(_h, 1);
        Children.Add(_v);
        Children.Add(_h);
        ClipToBounds = true;

        _surface.Owner = this;
        _surface.ScrollChanged += SyncScrollBars;
        _v.Scroll += (_, _) => _surface.SetOffset(_surface.OffsetX, _v.Value);
        _h.Scroll += (_, _) => _surface.SetOffset(_h.Value, _surface.OffsetY);
    }

    private void SyncScrollBars()
    {
        var s = _surface;
        _v.ViewportSize = s.ViewportHeight;
        _v.Maximum = Math.Max(0, s.ExtentHeight - s.ViewportHeight);
        _v.LargeChange = Math.Max(1, s.ViewportHeight * 0.9);
        _v.SmallChange = EpgSurface.RowHeight;
        _v.Value = s.OffsetY;
        _v.Visibility = _v.Maximum > 0 ? Visibility.Visible : Visibility.Collapsed;

        _h.ViewportSize = s.ViewportWidth;
        _h.Maximum = Math.Max(0, s.ExtentWidth - s.ViewportWidth);
        _h.LargeChange = Math.Max(1, s.ViewportWidth * 0.9);
        _h.SmallChange = EpgSurface.PxPerMinute * 30;
        _h.Value = s.OffsetX;
    }

    /// <summary>Vierittää nykyhetkeen.</summary>
    public void ScrollToNow() => _surface.ScrollToNow();

    // ---------- Riippuvuusominaisuudet ----------

    public static readonly DependencyProperty ChannelsProperty = DependencyProperty.Register(
        nameof(Channels), typeof(IList), typeof(EpgGrid), new PropertyMetadata(null, (d, e) =>
        {
            var g = (EpgGrid)d;
            // Järjestyksen muutokset (Move/Remove) piirretään heti ilman vierityksen nollausta
            if (e.OldValue is System.Collections.Specialized.INotifyCollectionChanged o) o.CollectionChanged -= g.OnCollectionChanged;
            if (e.NewValue is System.Collections.Specialized.INotifyCollectionChanged n) n.CollectionChanged += g.OnCollectionChanged;
            g._surface.OnChannelsChanged();
        }));

    private void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => _surface.OnItemsMoved();

    public static readonly DependencyProperty EpgProperty = DependencyProperty.Register(
        nameof(Epg), typeof(EpgService), typeof(EpgGrid), new PropertyMetadata(null, (d, _) => ((EpgGrid)d)._surface.InvalidateVisual()));

    public static readonly DependencyProperty SelectedChannelProperty = DependencyProperty.Register(
        nameof(SelectedChannel), typeof(LiveChannel), typeof(EpgGrid),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((EpgGrid)d)._surface.OnSelectionChanged()));

    /// <summary>Muuttuu, kun opas päivittyy → uudelleenpiirto.</summary>
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(EpgGrid), new PropertyMetadata(0, (d, _) => ((EpgGrid)d)._surface.OnGuideChanged()));

    public static readonly DependencyProperty ActivateCommandProperty = DependencyProperty.Register(
        nameof(ActivateCommand), typeof(ICommand), typeof(EpgGrid));

    public static readonly DependencyProperty FavoriteCommandProperty = DependencyProperty.Register(
        nameof(FavoriteCommand), typeof(ICommand), typeof(EpgGrid));

    public IList? Channels { get => (IList?)GetValue(ChannelsProperty); set => SetValue(ChannelsProperty, value); }
    public EpgService? Epg { get => (EpgService?)GetValue(EpgProperty); set => SetValue(EpgProperty, value); }
    public LiveChannel? SelectedChannel { get => (LiveChannel?)GetValue(SelectedChannelProperty); set => SetValue(SelectedChannelProperty, value); }
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    /// <summary>Tuplaklikkaus kanavaan (parametri: LiveChannel).</summary>
    public ICommand? ActivateCommand { get => (ICommand?)GetValue(ActivateCommandProperty); set => SetValue(ActivateCommandProperty, value); }
    /// <summary>Sydämen klikkaus (parametri: LiveChannel).</summary>
    public ICommand? FavoriteCommand { get => (ICommand?)GetValue(FavoriteCommandProperty); set => SetValue(FavoriteCommandProperty, value); }
}

internal sealed class EpgSurface : FrameworkElement
{
    public const double ChannelWidth = 230;
    public const double HeaderHeight = 38;
    public const double RowHeight = 58;
    public const double PxPerMinute = 6.4; // 30 min = 192 px

    private static readonly Typeface Regular = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface Semi = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface Icons = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Brush Bg = Frozen(Color.FromRgb(0x10, 0x10, 0x10));
    private static readonly Brush ChannelBg = Frozen(Color.FromRgb(0x16, 0x16, 0x16));
    private static readonly Brush HeaderBg = Frozen(Color.FromRgb(0x14, 0x14, 0x14));
    private static readonly Brush RowSelected = Frozen(Color.FromArgb(0x30, 0xE5, 0x09, 0x14));
    private static readonly Brush RowHover = Frozen(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Block = Frozen(Color.FromRgb(0x1F, 0x1F, 0x1F));
    private static readonly Brush BlockPast = Frozen(Color.FromRgb(0x18, 0x18, 0x18));
    private static readonly Brush BlockNow = Frozen(Color.FromRgb(0x2A, 0x1A, 0x1B));
    private static readonly Brush BlockHover = Frozen(Color.FromRgb(0x30, 0x30, 0x30));
    private static readonly Brush LogoBg = Frozen(Color.FromRgb(0x26, 0x26, 0x26));
    private static readonly Brush Text = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF2));
    private static readonly Brush TextDim = Frozen(Color.FromRgb(0xA0, 0xA0, 0xA0));
    private static readonly Brush TextFaint = Frozen(Color.FromRgb(0x6A, 0x6A, 0x6A));
    private static readonly Brush Accent = Frozen(Color.FromRgb(0xE5, 0x09, 0x14));
    private static readonly Pen GridLine = FrozenPen(Color.FromRgb(0x24, 0x24, 0x24), 1);
    private static readonly Pen TickPen = FrozenPen(Color.FromRgb(0x3A, 0x3A, 0x3A), 1);
    private static readonly Pen NowBorder = FrozenPen(Color.FromRgb(0xE5, 0x09, 0x14), 2);
    private static readonly Pen NowLine = FrozenPen(Color.FromRgb(0xE5, 0x09, 0x14), 2);

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double w) { var p = new Pen(new SolidColorBrush(c), w); p.Freeze(); return p; }

    public EpgGrid Owner { get; set; } = null!;
    public event Action? ScrollChanged;

    public double OffsetX { get; private set; }
    public double OffsetY { get; private set; }
    public double ViewportWidth => Math.Max(0, ActualWidth - ChannelWidth);
    public double ViewportHeight => Math.Max(0, ActualHeight - HeaderHeight);
    public double ExtentWidth => (_t1 - _t0).TotalMinutes * PxPerMinute;
    public double ExtentHeight => Count * RowHeight;

    private DateTime _t0, _t1;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private int _hoverRow = -1;
    private EpgProgram? _hoverProgram;
    private Point? _dragStart;
    private Point _dragOrigin;
    private bool _dragging;
    private bool _initialScrollDone;

    public EpgSurface()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
        ToolTipService.SetInitialShowDelay(this, 500);
        ToolTipService.SetShowDuration(this, 20000);
        _timer.Tick += (_, _) => InvalidateVisual();
        Loaded += (_, _) => { _timer.Start(); ComputeRange(); };
        Unloaded += (_, _) => _timer.Stop();
        SizeChanged += (_, _) => { ClampOffsets(); if (!_initialScrollDone && ActualWidth > 0) { _initialScrollDone = true; ScrollToNow(); } ScrollChanged?.Invoke(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) { ComputeRange(); InvalidateVisual(); } };
    }

    private IList? Channels => Owner.Channels;
    private int Count => Channels?.Count ?? 0;
    private LiveChannel? ChannelAt(int i) => i >= 0 && i < Count ? Channels![i] as LiveChannel : null;
    private IReadOnlyList<EpgProgram> ProgramsOf(LiveChannel ch) => Owner.Epg?.GetPrograms(ch) ?? [];

    // ---------- Aikaväli ----------

    private void ComputeRange()
    {
        var now = DateTime.Now;
        var start = now.AddHours(-2);
        start = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute >= 30 ? 30 : 0, 0);
        var end = now.AddHours(24);
        if (Owner.Epg is { IsLoaded: true } epg && epg.Range.end > end) end = epg.Range.end < now.AddDays(3) ? epg.Range.end : now.AddDays(3);
        var shift = _t0 == default ? 0 : (start - _t0).TotalMinutes * PxPerMinute;
        _t0 = start;
        _t1 = end;
        if (shift != 0) OffsetX -= shift; // pidetään näkymä paikallaan, kun alku siirtyy
        ClampOffsets();
        ScrollChanged?.Invoke();
    }

    public void OnGuideChanged() { ComputeRange(); InvalidateVisual(); }

    public void OnChannelsChanged()
    {
        OffsetY = 0;
        _hoverRow = -1;
        ClampOffsets();
        ScrollChanged?.Invoke();
        InvalidateVisual();
    }

    public void OnItemsMoved()
    {
        ClampOffsets();
        ScrollChanged?.Invoke();
        InvalidateVisual();
    }

    public void OnSelectionChanged()
    {
        // Vieritetään valittu kanava näkyviin
        if (Owner.SelectedChannel is { } sel && Channels is { } list)
        {
            var idx = list.IndexOf(sel);
            if (idx >= 0)
            {
                var top = idx * RowHeight;
                if (top < OffsetY) SetOffset(OffsetX, top);
                else if (top + RowHeight > OffsetY + ViewportHeight) SetOffset(OffsetX, top + RowHeight - ViewportHeight);
            }
        }
        InvalidateVisual();
    }

    public void ScrollToNow()
    {
        if (_t0 == default) ComputeRange();
        var x = (DateTime.Now - _t0).TotalMinutes * PxPerMinute - 30 * PxPerMinute;
        SetOffset(x, OffsetY);
    }

    public void SetOffset(double x, double y)
    {
        OffsetX = x;
        OffsetY = y;
        ClampOffsets();
        ScrollChanged?.Invoke();
        InvalidateVisual();
    }

    private void ClampOffsets()
    {
        OffsetX = Math.Max(0, Math.Min(OffsetX, ExtentWidth - ViewportWidth));
        OffsetY = Math.Max(0, Math.Min(OffsetY, ExtentHeight - ViewportHeight));
    }

    private double XOf(DateTime t) => ChannelWidth + (t - _t0).TotalMinutes * PxPerMinute - OffsetX;
    private DateTime TimeAtX(double x) => _t0.AddMinutes((x - ChannelWidth + OffsetX) / PxPerMinute);

    // ---------- Piirto ----------

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        if (_t0 == default) ComputeRange();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var now = DateTime.Now;

        dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));

        var first = Math.Max(0, (int)(OffsetY / RowHeight));
        var last = Math.Min(Count - 1, (int)((OffsetY + ViewportHeight) / RowHeight));
        var viewStart = TimeAtX(ChannelWidth);
        var viewEnd = TimeAtX(w);
        var selected = Owner.SelectedChannel;

        // --- Ohjelmalohkot ---
        dc.PushClip(new RectangleGeometry(new Rect(ChannelWidth, HeaderHeight, Math.Max(0, w - ChannelWidth), Math.Max(0, h - HeaderHeight))));

        // Puolen tunnin pystyviivat
        for (var t = _t0; t <= _t1; t = t.AddMinutes(30))
        {
            var x = XOf(t);
            if (x < ChannelWidth - 1 || x > w + 1) continue;
            dc.DrawLine(GridLine, new Point(Snap(x), HeaderHeight), new Point(Snap(x), h));
        }

        for (var i = first; i <= last; i++)
        {
            var ch = ChannelAt(i);
            if (ch is null) continue;
            var y = HeaderHeight + i * RowHeight - OffsetY;
            if (ch == selected) dc.DrawRectangle(RowSelected, null, new Rect(ChannelWidth, y, w - ChannelWidth, RowHeight));
            else if (i == _hoverRow) dc.DrawRectangle(RowHover, null, new Rect(ChannelWidth, y, w - ChannelWidth, RowHeight));

            var programs = ProgramsOf(ch);
            if (programs.Count == 0)
            {
                var r = new Rect(ChannelWidth + 3, y + 4, Math.Max(0, w - ChannelWidth - 6), RowHeight - 8);
                dc.DrawRoundedRectangle(BlockPast, null, r, 6, 6);
                DrawText(dc, "Ei ohjelmatietoja", Regular, 12, TextFaint, new Point(ChannelWidth + 14, y + RowHeight / 2 - 8), r.Width - 20, dpi);
                continue;
            }

            for (var k = FirstVisible(programs, viewStart); k < programs.Count; k++)
            {
                var p = programs[k];
                if (p.Start >= viewEnd) break;
                var x1 = XOf(p.Start);
                var x2 = XOf(p.End);
                var rect = new Rect(x1 + 2, y + 4, Math.Max(0, x2 - x1 - 4), RowHeight - 8);
                if (rect.Width < 1) continue;
                var isNow = p.IsNow(now);
                var fill = p == _hoverProgram ? BlockHover : isNow ? BlockNow : p.End <= now ? BlockPast : Block;
                dc.DrawRoundedRectangle(fill, isNow ? NowBorder : null, rect, 6, 6);

                // Teksti pysyy näkyvissä, vaikka ohjelma alkaisi vasemman reunan takaa
                var tx = Math.Max(rect.Left, ChannelWidth) + 10;
                var maxW = rect.Right - tx - 8;
                if (maxW < 14) continue;
                DrawText(dc, p.Title, Semi, 13, p.End <= now ? TextDim : Text, new Point(tx, y + 11), maxW, dpi);
                DrawText(dc, p.TimeText, Regular, 11, TextDim, new Point(tx, y + 31), maxW, dpi);
                if (isNow)
                {
                    // Pieni edistymispalkki lohkon alareunassa
                    var pw = (rect.Width - 12) * p.Progress(now);
                    dc.DrawRectangle(Accent, null, new Rect(rect.Left + 6, rect.Bottom - 5, Math.Max(0, pw), 2));
                }
            }
        }

        // Nykyhetken pystyviiva
        var nx = XOf(now);
        if (nx >= ChannelWidth && nx <= w) dc.DrawLine(NowLine, new Point(nx, HeaderHeight), new Point(nx, h));
        dc.Pop();

        // --- Kanavasarake ---
        dc.DrawRectangle(ChannelBg, null, new Rect(0, HeaderHeight, ChannelWidth, h - HeaderHeight));
        dc.PushClip(new RectangleGeometry(new Rect(0, HeaderHeight, ChannelWidth, Math.Max(0, h - HeaderHeight))));
        for (var i = first; i <= last; i++)
        {
            var ch = ChannelAt(i);
            if (ch is null) continue;
            var y = HeaderHeight + i * RowHeight - OffsetY;
            if (ch == selected)
            {
                dc.DrawRectangle(RowSelected, null, new Rect(0, y, ChannelWidth, RowHeight));
                dc.DrawRectangle(Accent, null, new Rect(0, y, 3, RowHeight));
            }
            else if (i == _hoverRow) dc.DrawRectangle(RowHover, null, new Rect(0, y, ChannelWidth, RowHeight));

            var logoRect = new Rect(12, y + (RowHeight - 36) / 2, 52, 36);
            dc.DrawRoundedRectangle(LogoBg, null, logoRect, 5, 5);
            var img = GetLogo(ch);
            if (img is not null) DrawImageFit(dc, img, new Rect(logoRect.X + 4, logoRect.Y + 3, logoRect.Width - 8, logoRect.Height - 6));

            DrawText(dc, ch.Name, Semi, 13, Text, new Point(76, y + 11), ChannelWidth - 76 - 40, dpi);
            if (ch.Number > 0) DrawText(dc, ch.Number.ToString(CultureInfo.InvariantCulture), Regular, 11, TextFaint, new Point(76, y + 31), 60, dpi);

            // Sydän
            DrawText(dc, ch.IsFavorite ? "" : "", Icons, 14, ch.IsFavorite ? Accent : TextFaint,
                new Point(ChannelWidth - 30, y + RowHeight / 2 - 9), 20, dpi);
            dc.DrawLine(GridLine, new Point(0, Snap(y + RowHeight)), new Point(ChannelWidth, Snap(y + RowHeight)));
        }
        dc.Pop();
        dc.DrawLine(TickPen, new Point(Snap(ChannelWidth), HeaderHeight), new Point(Snap(ChannelWidth), h));

        // --- Aikajana ---
        dc.DrawRectangle(HeaderBg, null, new Rect(0, 0, w, HeaderHeight));
        dc.PushClip(new RectangleGeometry(new Rect(ChannelWidth, 0, Math.Max(0, w - ChannelWidth), HeaderHeight)));
        for (var t = _t0; t <= _t1; t = t.AddMinutes(30))
        {
            var x = XOf(t);
            if (x < ChannelWidth - 80 || x > w) continue;
            dc.DrawLine(TickPen, new Point(Snap(x), HeaderHeight - 8), new Point(Snap(x), HeaderHeight));
            var label = t.Hour == 0 && t.Minute == 0 ? t.ToString("ddd d.M.", Fi) : t.ToString("HH:mm", Fi);
            DrawText(dc, label, t.Minute == 0 ? Semi : Regular, 12, t.Minute == 0 ? Text : TextDim, new Point(x + 6, 11), 120, dpi);
        }
        if (nx >= ChannelWidth && nx <= w)
        {
            var tag = new Rect(nx - 24, 8, 48, 20);
            dc.DrawRoundedRectangle(Accent, null, tag, 4, 4);
            DrawTextCentered(dc, now.ToString("HH:mm", Fi), Semi, 11, Brushes.White, tag, dpi);
        }
        dc.Pop();
        dc.DrawLine(TickPen, new Point(0, Snap(HeaderHeight)), new Point(w, Snap(HeaderHeight)));

        // Kulmasolu: päivä
        var dayText = viewStart.Date == now.Date ? "Tänään" : viewStart.Date == now.Date.AddDays(1) ? "Huomenna" : viewStart.ToString("ddd d.M.", Fi);
        DrawText(dc, dayText, Semi, 12, Text, new Point(14, 11), ChannelWidth - 20, dpi);

        if (Count == 0)
            DrawTextCentered(dc, Owner.Epg is { IsLoaded: false } ? Owner.Epg.Status : "Ei kanavia", Regular, 13, TextDim,
                new Rect(0, HeaderHeight, w, h - HeaderHeight), dpi);
        else if (Owner.Epg is { IsLoaded: false } epg)
            DrawTextCentered(dc, epg.Status, Regular, 12, TextDim, new Rect(ChannelWidth, h - 36, w - ChannelWidth, 30), dpi);
    }

    private static readonly CultureInfo Fi = CultureInfo.GetCultureInfo("fi-FI");

    private static double Snap(double v) => Math.Round(v) + 0.5;

    private static int FirstVisible(IReadOnlyList<EpgProgram> list, DateTime t)
    {
        int lo = 0, hi = list.Count - 1, ans = list.Count;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (list[mid].End > t) { ans = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        return ans;
    }

    private static void DrawText(DrawingContext dc, string text, Typeface tf, double size, Brush brush, Point at, double maxWidth, double dpi)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 4) return;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, brush, dpi)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(ft, at);
    }

    private static void DrawTextCentered(DrawingContext dc, string text, Typeface tf, double size, Brush brush, Rect r, double dpi)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, brush, dpi);
        dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
    }

    private static void DrawImageFit(DrawingContext dc, ImageSource img, Rect box)
    {
        var iw = img.Width; var ih = img.Height;
        if (iw <= 0 || ih <= 0) return;
        var s = Math.Min(box.Width / iw, box.Height / ih);
        var w = iw * s; var h = ih * s;
        dc.DrawImage(img, new Rect(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h));
    }

    private readonly HashSet<string> _pendingLogos = [];
    private ImageSource? GetLogo(LiveChannel ch)
    {
        if (ch.Logo is null) return null;
        var img = ImageCache.TryGet(ch.Logo, 104);
        if (img is not null || !_pendingLogos.Add(ch.Logo)) return img;
        var url = ch.Logo;
        _ = ImageCache.GetAsync(url, 104).ContinueWith(_ =>
        {
            Dispatcher.BeginInvoke(() => { _pendingLogos.Remove(url); InvalidateVisual(); }, DispatcherPriority.Background);
        }, TaskScheduler.Default);
        return null;
    }

    // ---------- Hiiri ja näppäimistö ----------

    private int RowAt(double y) => y < HeaderHeight ? -1 : (int)((y - HeaderHeight + OffsetY) / RowHeight);

    private EpgProgram? ProgramAt(Point p, out LiveChannel? ch)
    {
        ch = ChannelAt(RowAt(p.Y));
        if (ch is null || p.X < ChannelWidth) return null;
        var t = TimeAtX(p.X);
        var list = ProgramsOf(ch);
        var i = FirstVisible(list, t);
        return i < list.Count && list[i].Start <= t ? list[i] : null;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            SetOffset(OffsetX - e.Delta / 120.0 * PxPerMinute * 30, OffsetY);
        else
            SetOffset(OffsetX, OffsetY - e.Delta / 120.0 * RowHeight * 2);
        UpdateHover(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        var row = RowAt(p.Y);
        var ch = ChannelAt(row);

        if (e.ClickCount == 2)
        {
            if (ch is not null && Owner.ActivateCommand?.CanExecute(ch) == true) Owner.ActivateCommand.Execute(ch);
            e.Handled = true;
            return;
        }

        // Sydän kanavasarakkeessa
        if (ch is not null && p.X >= ChannelWidth - 38 && p.X < ChannelWidth)
        {
            if (Owner.FavoriteCommand?.CanExecute(ch) == true) Owner.FavoriteCommand.Execute(ch);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        _dragStart = p;
        _dragOrigin = new Point(OffsetX, OffsetY);
        _dragging = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_dragStart is { } s && e.LeftButton == MouseButtonState.Pressed)
        {
            var d = p - s;
            if (!_dragging && (Math.Abs(d.X) > 6 || Math.Abs(d.Y) > 6)) { _dragging = true; Cursor = Cursors.SizeAll; }
            if (_dragging) SetOffset(_dragOrigin.X - d.X, _dragOrigin.Y - d.Y);
            return;
        }
        UpdateHover(p);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragStart is null) return;
        var wasDrag = _dragging;
        var p = e.GetPosition(this);
        _dragStart = null;
        _dragging = false;
        Cursor = Cursors.Arrow;
        ReleaseMouseCapture();
        if (!wasDrag && ChannelAt(RowAt(p.Y)) is { } ch) Owner.SelectedChannel = ch;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverRow = -1;
        _hoverProgram = null;
        ToolTip = null;
        InvalidateVisual();
    }

    private void UpdateHover(Point p)
    {
        var row = RowAt(p.Y);
        var prog = ProgramAt(p, out _);
        if (row == _hoverRow && prog == _hoverProgram) return;
        _hoverRow = row;
        if (prog != _hoverProgram)
        {
            _hoverProgram = prog;
            ToolTip = prog is null ? null : BuildToolTip(prog);
        }
        Cursor = p.X >= ChannelWidth - 38 && p.X < ChannelWidth && row >= 0 ? Cursors.Hand : Cursors.Arrow;
        InvalidateVisual();
    }

    private static object BuildToolTip(EpgProgram p)
    {
        var panel = new StackPanel { MaxWidth = 360 };
        panel.Children.Add(new TextBlock { Text = p.Title, FontWeight = FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = $"{p.Start:ddd d.M.} {p.TimeText}", Foreground = TextDim, Margin = new Thickness(0, 2, 0, 6) });
        if (!string.IsNullOrWhiteSpace(p.Description))
            panel.Children.Add(new TextBlock { Text = p.Description, TextWrapping = TextWrapping.Wrap, Foreground = TextDim });
        return panel;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var list = Channels;
        if (list is null || list.Count == 0) return;
        var idx = Owner.SelectedChannel is { } s ? list.IndexOf(s) : -1;
        switch (e.Key)
        {
            case Key.Down: idx = Math.Min(list.Count - 1, idx + 1); break;
            case Key.Up: idx = Math.Max(0, idx - 1); break;
            case Key.PageDown: idx = Math.Min(list.Count - 1, idx + (int)(ViewportHeight / RowHeight)); break;
            case Key.PageUp: idx = Math.Max(0, idx - (int)(ViewportHeight / RowHeight)); break;
            case Key.Left: SetOffset(OffsetX - PxPerMinute * 30, OffsetY); e.Handled = true; return;
            case Key.Right: SetOffset(OffsetX + PxPerMinute * 30, OffsetY); e.Handled = true; return;
            case Key.Enter:
                if (Owner.SelectedChannel is { } c && Owner.ActivateCommand?.CanExecute(c) == true) Owner.ActivateCommand.Execute(c);
                e.Handled = true; return;
            default: return;
        }
        Owner.SelectedChannel = list[Math.Max(0, idx)] as LiveChannel;
        e.Handled = true;
    }
}
