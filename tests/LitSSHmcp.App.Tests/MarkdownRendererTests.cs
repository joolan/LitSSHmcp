using System.Threading;
using System.Windows.Controls;
using System.Windows.Documents;
using LitSSHmcp.App.Controls;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class MarkdownRendererTests
{
    // FlowDocument 及其元素有 Dispatcher 线程亲和性：渲染与断言都必须在同一个 STA 线程内完成。
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

    [Fact]
    public void Renders_table_with_header_and_rows() => OnSta(() =>
    {
        var doc = MarkdownRenderer.Render("| 名称 | 端口 |\n|---|---|\n| nginx | 80 |\n| mysql | 3306 |");

        var table = Assert.Single(doc.Blocks.OfType<Table>());
        Assert.Equal(3, table.RowGroups[0].Rows.Count);   // 表头 + 2 行
    });

    [Fact]
    public void Renders_fenced_code_block() => OnSta(() =>
    {
        var doc = MarkdownRenderer.Render("说明:\n\n```bash\nls -la\n```\n");

        var container = doc.Blocks.OfType<BlockUIContainer>().First();
        var border = container.Child as Border;
        var stack = border?.Child as System.Windows.Controls.StackPanel;
        var codeText = stack?.Children.OfType<System.Windows.Controls.TextBlock>().LastOrDefault();
        Assert.NotNull(codeText);
        Assert.Contains("ls -la", new System.Windows.Documents.TextRange(codeText!.ContentStart, codeText.ContentEnd).Text);
    });

    [Fact]
    public void Renders_heading_and_bold() => OnSta(() =>
    {
        var doc = MarkdownRenderer.Render("# 标题\n\n这是 **重点** 内容");

        Assert.Contains(doc.Blocks.OfType<Paragraph>(), p => p.FontWeight == System.Windows.FontWeights.Bold);
    });
}
