using LitSSHmcp.Core.Services.Sql;
using Xunit;

namespace LitSSHmcp.Tests;

public class SqlScriptFormatterTests
{
    [Fact]
    public void Uppercases_keywords_and_breaks_major_clauses()
    {
        var formatted = SqlScriptFormatter.Format("select id, name from users where age > 18 order by name");
        Assert.Contains("SELECT", formatted);
        Assert.Contains("\nFROM", formatted);
        Assert.Contains("\nWHERE", formatted);
        Assert.Contains("\nORDER BY", formatted);
        Assert.DoesNotContain("select", formatted);
    }

    [Fact]
    public void Preserves_string_literals()
    {
        var formatted = SqlScriptFormatter.Format("select * from t where name = 'Select From'");
        Assert.Contains("'Select From'", formatted);
    }

    [Fact]
    public void Handles_null_and_empty()
    {
        Assert.Equal(string.Empty, SqlScriptFormatter.Format(""));
        Assert.Equal(string.Empty, SqlScriptFormatter.Format(null));
    }

    [Fact]
    public void Function_calls_have_no_space_before_paren()
    {
        var formatted = SqlScriptFormatter.Format("select count(*) from t");
        Assert.Contains("COUNT(*)", formatted);
    }
}
