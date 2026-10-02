using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>导出图片的核心前提：RenderTargetBitmap.Render 会应用根级 RenderTransform，
/// 且子元素按世界坐标(Canvas.Left/Top)渲染——对应 OnExportImage 的平移截取逻辑。</summary>
public class RenderTargetExportTests
{
    [Fact]
    public void RenderTargetBitmap_AppliesRootTranslateTransform()
    {
        // WPF 组件要求 STA，xunit 默认 MTA —— 自建 STA 线程执行渲染
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                RenderAndAssert();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void RenderAndAssert()
    {
        var canvas = new Canvas { Width = 400, Height = 300, Background = Brushes.White };
        var rect = new Rectangle { Width = 100, Height = 50, Fill = Brushes.Red };
        Canvas.SetLeft(rect, 200);
        Canvas.SetTop(rect, 100);
        canvas.Children.Add(rect);

        // 模拟导出区域起点 (180, 80)：内容应整体平移 (-180, -80)
        canvas.RenderTransform = new TranslateTransform(-180, -80);

        // 未上屏的可视树需手动布局，否则渲染为空（应用内 GraphCanvas 已在屏上，天然已布局）
        canvas.Measure(new Size(400, 300));
        canvas.Arrange(new Rect(0, 0, 400, 300));

        var bmp = new RenderTargetBitmap(400, 300, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(canvas);

        var px = new byte[4];

        // 红块世界坐标 (200..300, 100..150) -> 位图 (20..120, 20..70)：取中心 (60, 40) 应为红
        bmp.CopyPixels(new Int32Rect(60, 40, 1, 1), px, 4, 0);
        Assert.Equal(0, px[0]);   // B
        Assert.Equal(255, px[2]); // R

        // 区域内但无内容处 (5, 5) 应为白背景
        bmp.CopyPixels(new Int32Rect(5, 5, 1, 1), px, 4, 0);
        Assert.Equal(255, px[0]);
        Assert.Equal(255, px[2]);
        Assert.Equal(255, px[3]);
    }
}
