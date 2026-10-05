using System.Threading;
using System.Windows.Documents;
using LitSSHmcp.App.Controls;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class SyntaxHighlighterTests
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

    [Fact]
    public void Bash_colors_keywords_strings_comments_numbers() => OnSta(() =>
    {
        var tokens = SyntaxHighlighter.Highlight("echo \"hi\" # note\n123", "bash");

        Assert.Contains(tokens, t => t.Text == "echo" && ReferenceEquals(t.Brush, SyntaxHighlighter.KeywordBrush));
        Assert.Contains(tokens, t => t.Text == "\"hi\"" && ReferenceEquals(t.Brush, SyntaxHighlighter.StringBrush));
        Assert.Contains(tokens, t => t.Text.Contains("# note") && ReferenceEquals(t.Brush, SyntaxHighlighter.CommentBrush));
        Assert.Contains(tokens, t => t.Text == "123" && ReferenceEquals(t.Brush, SyntaxHighlighter.NumberBrush));
    });

    [Fact]
    public void Sql_marks_keywords_and_line_comment() => OnSta(() =>
    {
        var tokens = SyntaxHighlighter.Highlight("SELECT 1 -- 注释\nFROM t", "sql");
        Assert.Contains(tokens, t => t.Text.ToUpperInvariant() == "SELECT" && ReferenceEquals(t.Brush, SyntaxHighlighter.KeywordBrush));
        Assert.Contains(tokens, t => t.Text.StartsWith("--") && ReferenceEquals(t.Brush, SyntaxHighlighter.CommentBrush));
    });
}
