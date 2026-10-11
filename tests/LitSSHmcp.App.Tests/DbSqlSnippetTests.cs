using LitSSHmcp.App.ViewModels;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class DbSqlSnippetTests
{
    [Fact]
    public void Display_uses_name_when_present()
    {
        var s = new DbSqlSnippet("SELECT 1", "健康检查");
        Assert.Equal("健康检查", s.Display);
    }

    [Fact]
    public void Display_falls_back_to_sql_preview()
    {
        var s = new DbSqlSnippet("SELECT * FROM orders", null);
        Assert.Equal("SELECT * FROM orders", s.Display);
    }

    [Fact]
    public void Display_truncates_long_sql()
    {
        var sql = new string('a', 80);
        var s = new DbSqlSnippet(sql, null);
        Assert.EndsWith("…", s.Display);
        Assert.True(s.Display.Length <= 49);
    }

    [Fact]
    public void AppendSql_sets_when_current_empty()
        => Assert.Equal("SELECT 1", DbWorkbenchViewModel.AppendSql("", "SELECT 1"));

    [Fact]
    public void AppendSql_appends_with_newline()
    {
        var result = DbWorkbenchViewModel.AppendSql("SELECT 1", "SELECT 2");
        Assert.Equal("SELECT 1" + System.Environment.NewLine + "SELECT 2", result);
    }
}
