using System.Windows;
using System.Windows.Media;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.Controls;

/// <summary>
/// 上下文占用环形进度（图标化）：<see cref="Percent"/> 0..1，占用越高颜色越警示（强调→警告→危险）。
/// 用 OnRender 直接绘制圆弧，无第三方依赖。
/// </summary>
public sealed class ContextRing : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(ContextRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public ContextRing()
    {
        Loaded += (_, _) => ThemeService.ThemeChanged += InvalidateVisual;
        Unloaded += (_, _) => ThemeService.ThemeChanged -= InvalidateVisual;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 4)
            return;

        var thickness = Math.Max(2.0, size * 0.16);
        var radius = size / 2 - thickness / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        var track = Resolve("AppBorderBrush", () => new SolidColorBrush(Color.FromArgb(0x55, 0x88, 0x88, 0x88)));
        var value = Math.Clamp(Percent, 0, 1);
        var brush = value >= 0.85 ? Resolve("AppDangerBrush", () => Brushes.OrangeRed)
            : value >= 0.6 ? Resolve("AppWarningBrush", () => Brushes.Goldenrod)
            : Resolve("AppAccentBrush", () => Brushes.DodgerBlue);

        dc.DrawEllipse(null, new Pen(track, thickness), center, radius, radius);

        if (value <= 0.0001)
            return;

        var startAngle = -90.0;
        var sweep = 360.0 * value;
        var start = PointOnCircle(center, radius, startAngle);
        var end = PointOnCircle(center, radius, startAngle + sweep);

        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        dc.DrawGeometry(null, new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private static Brush Resolve(string key, Func<Brush> fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback();
}
