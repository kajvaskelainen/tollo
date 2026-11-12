using System.Windows;
using System.Windows.Media;

namespace Kanava.Controls;

/// <summary>
/// VideoView on HwndHost, jota WPF ei voi leikata pyöreäksi ("airspace").
/// Tämä piirretään VideoView:n overlay-sisältöön ja maalaa kulmat taustavärillä,
/// jolloin video näyttää pyöristetyltä.
/// </summary>
public sealed class CornerMask : FrameworkElement
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(CornerMask), new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(CornerMask), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public CornerMask() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        var r = Radius;
        var w = ActualWidth;
        var h = ActualHeight;
        if (r <= 0 || w <= 0 || h <= 0) return;
        var outer = new RectangleGeometry(new Rect(0, 0, w, h));
        var inner = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        var mask = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
        mask.Freeze();
        dc.DrawGeometry(Fill, null, mask);
    }
}
