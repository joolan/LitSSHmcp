using System.Linq;
using LitSSHmcp.Core.Services.Sql;
using Xunit;

namespace LitSSHmcp.Tests;

public class SqlScriptSplitterTests
{
    private static string[] Texts(string sql) =>
        SqlScriptSplitter.Split(sql).Select(s => sql.Substring(s.Start, s.Length)).ToArray();

    [Fact]
    public void Splits_two_statements()
        => Assert.Equal(new[] { "select 1", "select 2" }, Texts("select 1; select 2"));

    [Fact]
    public void Ignores_empty_and_trailing_semicolons()
    {
        Assert.Equal(new[] { "select 1" }, Texts("  select 1 ;  "));
        Assert.Empty(Texts("  ;;  \n "));
    }

    [Fact]
    public void Semicolon_inside_single_quote_is_not_a_separator()
        => Assert.Equal(new[] { "select ';'", "select 2" }, Texts("select ';'; select 2"));

    [Fact]
    public void Escaped_quote_is_handled()
        => Assert.Equal(new[] { "select 'a''b;c'" }, Texts("select 'a''b;c'"));

    [Fact]
    public void Semicolon_in_line_comment_is_ignored()
        => Assert.Equal(new[] { "select 1 -- ;x", "select 2" }, Texts("select 1 -- ;x\n; select 2"));

    [Fact]
    public void Semicolon_in_block_comment_is_ignored()
        => Assert.Equal(new[] { "select /* ; */ 1", "select 2" }, Texts("select /* ; */ 1; select 2"));

    [Fact]
    public void Nested_block_comment_is_handled()
        => Assert.Equal(new[] { "select /* a /* b; */ c */ 1" }, Texts("select /* a /* b; */ c */ 1"));

    [Fact]
    public void Semicolon_in_dollar_quote_is_ignored()
        => Assert.Equal(new[] { "select $$a;b$$", "select 2" }, Texts("select $$a;b$$; select 2"));

    [Fact]
    public void Backtick_identifier_with_semicolon_is_ignored()
        => Assert.Equal(new[] { "select `a;b` from t" }, Texts("select `a;b` from t"));

    [Fact]
    public void StatementAt_picks_statement_under_caret()
    {
        const string sql = "select 1;\nselect 22;\nselect 333";
        var stmt = SqlScriptSplitter.StatementAt(sql, sql.IndexOf("22", System.StringComparison.Ordinal));
        Assert.NotNull(stmt);
        Assert.Equal("select 22", sql.Substring(stmt!.Value.Start, stmt.Value.Length));
    }

    [Fact]
    public void StatementAt_returns_null_when_no_statements()
        => Assert.Null(SqlScriptSplitter.StatementAt("   -- only comment\n", 0));
}
