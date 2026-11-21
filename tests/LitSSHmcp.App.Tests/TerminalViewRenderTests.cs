using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LitSSHmcp.App.Controls;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>
/// 终端自绘冒烟测试：验证行绘制缓存（DrawingGroup 复用）在输出、滚动、resize 后都能正常渲染。
/// </summary>
public class TerminalViewRenderTests
{
    private static Exception? RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return captured;
    }

    [Fact]
    public void Renders_after_output_scroll_and_resize_with_row_cache()
    {
        var ex = RunSta(() =>
        {
            var model = new TerminalModel(20, 5);
            model.Feed("hello\r\nworld");

            var view = new TerminalView
            {
                Session = new FakeSession(model),
                Width = 240,
                Height = 120
            };
            view.Measure(new Size(240, 120));
            view.Arrange(new Rect(0, 0, 240, 120));
            view.UpdateLayout();

            var bitmap = new RenderTargetBitmap(240, 120, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);

            // 追加输出（触发滚动/新行）
            model.Feed("\r\nmore output\r\nline 4");
            bitmap.Render(view);

            // 宽度变化（触发 resize 与行缓存清理）
            model.Resize(40, 10);
            view.Measure(new Size(240, 120));
            view.Arrange(new Rect(0, 0, 240, 120));
            bitmap.Render(view);

            // 纯光标刷新（不应重绘行内容，但必须可渲染）
            bitmap.Render(view);
        });

        Assert.Null(ex);
    }

    private sealed class FakeSession(TerminalModel model) : ITerminalSession
    {
        public TerminalModel Model { get; } = model;
        public Task EnsureStartedAsync(int cols, int rows) => Task.CompletedTask;
        public void SendInput(string text) { }
        public void SendUserInput(string text) { }
        public void Resize(int cols, int rows) => Model.Resize(cols, rows);
    }
}
