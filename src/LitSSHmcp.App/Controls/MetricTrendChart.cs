using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LitSSHmcp.App.Controls;

public sealed record MetricSeries(string Name, Color Color, IReadOnlyList<double> Values);

/// <summary>主页本机趋势图：固定 0..100 纵轴，多条折线 + 面积填充，无第三方图表依赖。</summary>
public sealed class MetricTrendChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<MetricSeries>), typeof(MetricTrendChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StartLabelProperty = DependencyProperty.Register(
        nameof(StartLabel), typeof(string), typeof(MetricTrendChart),
        new FrameworkPropertyMetadata("60 秒前", FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<MetricSeries>? Series
    {
        get => (IReadOnlyList<MetricSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public string StartLabel
    {
        get => (string)GetValue(StartLabelProperty);
        set => SetValue(StartLabelProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 8 || height <= 8)
            return;

        var axis = new Pen(new SolidColorBrush(Color.FromArgb(110, 128, 128, 128)), 1);
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(42, 128, 128, 128)), 1);
        var textBrush = Resolve("AppTextTertiaryBrush", () => new SolidColorBrush(Color.FromArgb(190, 140, 140, 140)));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        const double left = 38, right = 12, top = 26, bottom = 16;
        var plotWidth = Math.Max(1, width - left - right);
        var plotHeight = Math.Max(1, height - top - bottom);

        FormattedText Label(string value, double size = 10) =>
            new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Consolas"), size, textBrush, dpi);

        for (var i = 0; i <= 4; i++)
        {
            var value = i * 25;
            var y = top + plotHeight * (1 - value / 100.0);
            dc.DrawLine(i is 0 or 4 ? axis : grid, new Point(left, y), new Point(left + plotWidth, y));
            var label = Label(value.ToString(CultureInfo.InvariantCulture));
            dc.DrawText(label, new Point(left - label.Width - 6, y - label.Height / 2));
        }

        dc.DrawLine(axis, new Point(left, top), new Point(left, top + plotHeight));
        dc.DrawLine(axis, new Point(left, top + plotHeight), new Point(left + plotWidth, top + plotHeight));
        dc.DrawText(Label(string.IsNullOrWhiteSpace(StartLabel) ? "60 秒前" : StartLabel, 9),
            new Point(left, top + plotHeight + 3));

        var series = Series;
        if (series is null || series.Count == 0 || series.All(s => s.Values.Count < 2))
        {
            var empty = Label("正在采集本机指标…", 11);
            dc.DrawText(empty, new Point(left + (plotWidth - empty.Width) / 2, top + plotHeight / 2 - empty.Height / 2));
            return;
        }

        var north = Label("现在", 9);
        dc.DrawText(north, new Point(left + plotWidth - north.Width, top + plotHeight + 3));

        var unit = Label("使用率 %", 9);
        dc.DrawText(unit, new Point(left + plotWidth - unit.Width, 6));

        var legendX = left;
        foreach (var item in series)
        {
            if (item.Values.Count < 2)
                continue;

            var count = item.Values.Count;
            var step = plotWidth / (count - 1);
            Point PointAt(int index)
            {
                var x = left + step * index;
                var y = top + plotHeight * (1 - Math.Clamp(item.Values[index], 0, 100) / 100.0);
                return new Point(x, y);
            }

            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(PointAt(0), false, false);
                for (var i = 1; i < count; i++)
                    ctx.LineTo(PointAt(i), true, false);
            }
            line.Freeze();

            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(left, top + plotHeight), true, true);
                for (var i = 0; i < count; i++)
                    ctx.LineTo(PointAt(i), true, false);
                ctx.LineTo(new Point(left + plotWidth, top + plotHeight), true, false);
            }
            area.Freeze();

            var fill = new LinearGradientBrush(
                Color.FromArgb(0x14, item.Color.R, item.Color.G, item.Color.B),
                Color.FromArgb(0x00, item.Color.R, item.Color.G, item.Color.B),
                new Point(0, 0), new Point(0, 1));
            fill.Freeze();
            dc.DrawGeometry(fill, null, area);

            var pen = new Pen(new SolidColorBrush(item.Color), 1.8)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, line);

            dc.DrawRectangle(new SolidColorBrush(item.Color), null, new Rect(legendX, 8, 9, 9));
            var legend = Label(item.Name, 10);
            dc.DrawText(legend, new Point(legendX + 13, 5));
            legendX += 13 + legend.Width + 16;
        }
    }

    private static Brush Resolve(string key, Func<Brush> fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback();
}
