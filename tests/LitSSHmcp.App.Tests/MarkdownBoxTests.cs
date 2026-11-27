using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Controls;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class MarkdownBoxTests
{
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
            throw error;
    }

    /// <summary>
    /// 回归：文档内按钮获得焦点时 FlowDocumentScrollViewer 会对自己发起 BringIntoView，
    /// 外层聊天 ScrollViewer 曾因此把整个回答滚到顶部（点代码块“复制”页面跳动）。
    /// </summary>
    [Fact]
    public void Self_bring_into_view_request_is_suppressed() => OnSta(() =>
    {
        var box = new MarkdownBox { Markdown = "```bash\nls -la\n```" };
        var host = new Border { Child = box };

        var handled = false;
        host.AddHandler(FrameworkElement.RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler((_, e) => handled |= e.Handled),
            handledEventsToo: true);

        // WPF 没有公开 RequestBringIntoViewEventArgs 的构造函数，用内部构造函数模拟真实事件。
        var ctor = typeof(RequestBringIntoViewEventArgs).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            new[] { typeof(DependencyObject), typeof(Rect) }, modifiers: null);
        Assert.NotNull(ctor);
        var args = Assert.IsType<RequestBringIntoViewEventArgs>(
            ctor!.Invoke(new object[] { box, new Rect(0, 0, 40, 20) }));
        args.RoutedEvent = FrameworkElement.RequestBringIntoViewEvent;
        box.RaiseEvent(args);

        Assert.True(handled);
    });

    [Fact]
    public void Streaming_renders_lightweight_code_and_final_render_is_selectable() => OnSta(() =>
    {
        var box = new MarkdownBox
        {
            Markdown = "```bash\nls -la\n```",
            IsStreaming = true
        };

        Assert.Empty(CodeBoxes(box.Document));
        Assert.NotEmpty(PlainCodeBlocks(box.Document));

        box.IsStreaming = false;

        var codeBox = Assert.Single(CodeBoxes(box.Document));
        Assert.True(codeBox.IsReadOnly);
    });

    private static IEnumerable<RichTextBox> CodeBoxes(System.Windows.Documents.FlowDocument document) =>
        document.Blocks.OfType<System.Windows.Documents.BlockUIContainer>()
            .Select(c => (c.Child as Border)?.Child as StackPanel)
            .Where(s => s is not null)
            .SelectMany(s => s!.Children.OfType<RichTextBox>());

    private static IEnumerable<TextBlock> PlainCodeBlocks(System.Windows.Documents.FlowDocument document) =>
        document.Blocks.OfType<System.Windows.Documents.BlockUIContainer>()
            .Select(c => (c.Child as Border)?.Child as StackPanel)
            .Where(s => s is not null)
            .SelectMany(s => s!.Children.OfType<TextBlock>());

    /// <summary>
    /// 回归：复制按钮若可获焦，首次点击会让焦点进入文档，触发外层聊天区的 BringIntoView，
    /// 表现为第一次点「复制」页面跳一下、第二次正常。复制按钮应不参与焦点。
    /// </summary>
    [Fact]
    public void Code_copy_buttons_do_not_take_focus() => OnSta(() =>
    {
        var box = new MarkdownBox
        {
            Markdown = "```bash\nls -la\n```\n\n```bash\npwd\n```",
            IsStreaming = false
        };

        var buttons = CopyButtons(box.Document).ToList();
        Assert.Equal(2, buttons.Count);
        Assert.All(buttons, button =>
        {
            Assert.False(button.Focusable);
            Assert.False(button.IsTabStop);
        });
    });

    private static IEnumerable<Button> CopyButtons(System.Windows.Documents.FlowDocument document) =>
        document.Blocks.OfType<System.Windows.Documents.BlockUIContainer>()
            .Select(c => (c.Child as Border)?.Child as StackPanel)
            .Where(s => s is not null)
            .SelectMany(s => s!.Children.OfType<DockPanel>())
            .SelectMany(d => d.Children.OfType<Button>());
}
