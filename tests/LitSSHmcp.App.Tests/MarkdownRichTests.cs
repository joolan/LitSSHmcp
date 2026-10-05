using System.Threading;
using System.Windows.Controls;
using System.Windows.Documents;
using LitSSHmcp.App.Controls;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class MarkdownRichTests
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
        if (error is not null) throw error;
    }

    private static string TextOf(TextElement element) => new TextRange(element.ContentStart, element.ContentEnd).Text;

    [Fact]
    public void Renders_task_list_items() => OnSta(() =>
    {
        var doc = MarkdownRenderer.Render("- [x] 已完成\n- [ ] 待办");

        var list = doc.Blocks.OfType<List>().First();
        var items = list.ListItems.Cast<ListItem>().ToList();
        Assert.Contains("☑", TextOf(items[0].Blocks.OfType<Paragraph>().First()));
        Assert.Contains("☐", TextOf(items[1].Blocks.OfType<Paragraph>().First()));
    });

    [Fact]
    public void Renders_nested_list() => OnSta(() =>
    {
        var doc = MarkdownRenderer.Render("- 父项\n  - 子项");

        var root = doc.Blocks.OfType<List>().First();
        var parent = root.ListItems.Cast<ListItem>().First();
        Assert.Contains(parent.Blocks, b => b is List);   // 父项内含子列表
    });

    [Fact]
    public void Renders_footnote_reference_and_section() => OnSta(() =>
    {
        var doc = MarkdownRenderer.Render("正文引用[^1]\n\n[^1]: 这是脚注内容");

        var allText = string.Join(" ", doc.Blocks.OfType<Paragraph>().Select(TextOf));
        Assert.Contains("[1]", allText);
        Assert.Contains("这是脚注内容", allText);
    });

    [Fact]
    public void Renders_code_block_with_copy_button() => OnSta(() =>
    {
        var doc = MarkdownRenderer.Render("```bash\nls -la\n```");

        var container = doc.Blocks.OfType<BlockUIContainer>().First();
        var border = container.Child as Border;
        var stack = border?.Child as StackPanel;
        var dock = stack?.Children.OfType<DockPanel>().FirstOrDefault();
        Assert.NotNull(dock);
        Assert.Contains(dock!.Children.OfType<Button>(), b => (string)b.Content == "复制");
    });

    [Fact]
    public void Renders_image_as_block() => OnSta(() =>
    {
        var doc = MarkdownRenderer.Render("![架构图](https://example.com/a.png)");

        var container = doc.Blocks.OfType<BlockUIContainer>().First();
        Assert.IsType<Image>(container.Child);
    });
}
