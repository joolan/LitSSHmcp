using System.Windows;
using System.Windows.Media;

namespace LitSSHmcp.App.Controls;

/// <summary>指标迷你趋势线：自动归一化，带低透明度面积填充；用于主页指标卡。</summary>
public sealed class MetricSparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(MetricSparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(MetricSparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        if (values is null || values.Count < 2 || ActualWidth <= 4 || ActualHeight <= 4)
            return;

        var stroke = Stroke ?? Resolve("AppAccentBrush", () => Brushes.DodgerBlue);
        var max = Math.Max(1.0, values.Max());
        var min = Math.Min(0.0, values.Min());
        var range = Math.Max(1.0, max - min);

        const double pad = 2;
        var width = Math.Max(1, ActualWidth - pad * 2);
        var height = Math.Max(1, ActualHeight - pad * 2);
        var step = width / (values.Count - 1);

        Point PointAt(int index)
        {
            var x = pad + step * index;
            var normalized = (Math.Clamp(values[index], min, max) - min) / range;
            var y = pad + height * (1 - normalized);
            return new Point(x, y);
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(PointAt(0), false, false);
            for (var i = 1; i < values.Count; i++)
                ctx.LineTo(PointAt(i), true, false);
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(pad, pad + height), true, true);
            for (var i = 0; i < values.Count; i++)
                ctx.LineTo(PointAt(i), true, false);
            ctx.LineTo(new Point(pad + width, pad + height), true, false);
        }
        area.Freeze();

        var color = (stroke as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
        var fillBrush = new LinearGradientBrush(
            Color.FromArgb(0x48, color.R, color.G, color.B),
            Color.FromArgb(0x00, color.R, color.G, color.B),
            new Point(0, 0), new Point(0, 1));
        fillBrush.Freeze();

        dc.DrawGeometry(fillBrush, null, area);
        var pen = new Pen(stroke, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }

    private static Brush Resolve(string key, Func<Brush> fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback();
}
