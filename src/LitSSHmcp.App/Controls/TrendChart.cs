using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LitSSHmcp.App.Controls;

/// <summary>一条趋势序列（名称 + 颜色 + 与时间点一一对应的值；<c>null</c> 表示缺测点）。</summary>
public sealed record TrendSeries(string Name, Color Color, IReadOnlyList<double?> Values);

/// <summary>
/// 极简折线趋势图：纵轴固定 0..100（百分比口径），等距横轴；仅用 DrawingContext 手绘，无第三方图表库。
/// </summary>
public sealed class TrendChart : FrameworkElement
{
    private IReadOnlyList<TrendSeries> _series = Array.Empty<TrendSeries>();
    private IReadOnlyList<DateTime> _times = Array.Empty<DateTime>();

    public void SetData(IReadOnlyList<TrendSeries> series, IReadOnlyList<DateTime> times)
    {
        _series = series;
        _times = times;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        var axis = new Pen(new SolidColorBrush(Color.FromArgb(110, 128, 128, 128)), 1);
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(45, 128, 128, 128)), 1);
        var text = new SolidColorBrush(Color.FromArgb(200, 130, 130, 130));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        const double left = 48, right = 14, top = 26, bottom = 30;
        var plotW = Math.Max(1, w - left - right);
        var plotH = Math.Max(1, h - top - bottom);

        FormattedText Label(string s, double size = 10) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Consolas"), size, text, dpi);

        // 纵轴网格 + 刻度（0/25/50/75/100）
        for (var i = 0; i <= 4; i++)
        {
            var value = i * 25;
            var y = top + plotH * (1 - value / 100.0);
            dc.DrawLine(i is 0 or 4 ? axis : grid, new Point(left, y), new Point(left + plotW, y));
            var ft = Label(value.ToString());
            dc.DrawText(ft, new Point(left - ft.Width - 6, y - ft.Height / 2));
        }

        var n = _times.Count;
        if (n == 0 || _series.Count == 0)
        {
            dc.DrawText(Label("（暂无数据，先在某台服务器上采集快照）"), new Point(left, top + plotH / 2));
            return;
        }

        // 纵轴线
        dc.DrawLine(axis, new Point(left, top), new Point(left, top + plotH));
        dc.DrawLine(axis, new Point(left, top + plotH), new Point(left + plotW, top + plotH));

        double X(int index) => left + (n == 1 ? plotW / 2 : plotW * index / (double)(n - 1));
        double Y(double value) => top + plotH * (1 - Math.Clamp(value, 0, 100) / 100.0);

        // 横轴首/中/末时间标签
        void DrawTime(int index)
        {
            if (index < 0 || index >= n)
                return;
            var ft = Label(_times[index].ToString("MM-dd HH:mm"), 9.5);
            var x = Math.Clamp(X(index) - ft.Width / 2, left, left + plotW - ft.Width);
            dc.DrawText(ft, new Point(x, top + plotH + 6));
        }
        DrawTime(0);
        DrawTime(n / 2);
        DrawTime(n - 1);

        // 折线
        foreach (var series in _series)
        {
            var brush = new SolidColorBrush(series.Color);
            var pen = new Pen(brush, 1.8);
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                var started = false;
                for (var i = 0; i < n && i < series.Values.Count; i++)
                {
                    var v = series.Values[i];
                    if (v is null)
                    {
                        started = false;
                        continue;
                    }
                    var p = new Point(X(i), Y(v.Value));
                    if (!started)
                    {
                        ctx.BeginFigure(p, false, false);
                        started = true;
                    }
                    else
                    {
                        ctx.LineTo(p, true, false);
                    }
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        // 图例
        var lx = left;
        foreach (var series in _series)
        {
            dc.DrawRectangle(new SolidColorBrush(series.Color), null, new Rect(lx, 8, 10, 10));
            var ft = Label(series.Name, 10);
            dc.DrawText(ft, new Point(lx + 14, 6));
            lx += 14 + ft.Width + 16;
        }
    }
}
