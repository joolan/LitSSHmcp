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
        var codeBox = stack?.Children.OfType<System.Windows.Controls.RichTextBox>().LastOrDefault();
        Assert.NotNull(codeBox);
        Assert.True(codeBox!.IsReadOnly);
        Assert.False(codeBox.IsTabStop);
        var codeText = new System.Windows.Documents.TextRange(codeBox.Document.ContentStart, codeBox.Document.ContentEnd).Text;
        Assert.Contains("ls -la", codeText);
    });

    [Fact]
    public void Streaming_code_block_uses_lightweight_textblock() => OnSta(() =>
    {
        var doc = MarkdownRenderer.Render("```bash\nls -la\n```", selectableCode: false);

        var stack = (doc.Blocks.OfType<BlockUIContainer>().First().Child as Border)?.Child as System.Windows.Controls.StackPanel;
        Assert.NotNull(stack);
        Assert.Empty(stack!.Children.OfType<System.Windows.Controls.RichTextBox>());
        Assert.Contains("ls -la", new System.Windows.Documents.TextRange(
            stack.Children.OfType<System.Windows.Controls.TextBlock>().Last().ContentStart,
            stack.Children.OfType<System.Windows.Controls.TextBlock>().Last().ContentEnd).Text);
    });

    [Fact]
    public void Code_block_keeps_syntax_highlight_runs_and_is_selectable() => OnSta(() =>
    {
        var doc = MarkdownRenderer.Render("```bash\necho \"hi\" # 注释\n```");

        var codeBox = Assert.IsType<System.Windows.Controls.RichTextBox>(
            (doc.Blocks.OfType<BlockUIContainer>().First().Child as Border)?.Child is System.Windows.Controls.StackPanel stack
                ? stack.Children.OfType<System.Windows.Controls.RichTextBox>().Last()
                : null);
        Assert.True(codeBox.IsReadOnly);
        Assert.True(codeBox.IsInactiveSelectionHighlightEnabled);

        var runs = codeBox.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Run>().ToList();
        Assert.Contains(runs, r => r.Text.Contains("注释"));               // 注释仍单独着色
        Assert.Contains(runs, r => r.Foreground is not null);
        Assert.Equal("echo \"hi\" # 注释", new TextRange(codeBox.Document.ContentStart, codeBox.Document.ContentEnd).Text.Trim());
    });

    [Fact]
    public void Renders_heading_and_bold() => OnSta(() =>
    {
        var doc = MarkdownRenderer.Render("# 标题\n\n这是 **重点** 内容");

        Assert.Contains(doc.Blocks.OfType<Paragraph>(), p => p.FontWeight == System.Windows.FontWeights.Bold);
    });

    [Theory]
    [InlineData("https://example.com/docs")]
    [InlineData("http://example.com")]
    [InlineData("mailto:ops@example.com")]
    public void Allows_only_external_safe_link_schemes(string url) => OnSta(() =>
    {
        var doc = MarkdownRenderer.Render($"[链接]({url})");

        var link = Assert.Single(doc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Hyperlink>());
        Assert.Equal(url, link.NavigateUri.OriginalString);
    });

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("javascript:alert(1)")]
    public void Blocks_local_and_unknown_link_schemes(string url) => OnSta(() =>
    {
        var doc = MarkdownRenderer.Render($"[链接]({url})");

        var inlines = doc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).ToList();
        Assert.Empty(inlines.OfType<Hyperlink>());
        Assert.Contains(inlines.OfType<Run>(), r => r.Text == "链接");
    });
}
