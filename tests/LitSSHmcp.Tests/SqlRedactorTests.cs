using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.Tests;

public class SqlRedactorTests
{
    [Fact]
    public void Masks_string_literals()
    {
        Assert.Equal("SELECT * FROM t WHERE name = '?'", SqlRedactor.Mask("SELECT * FROM t WHERE name = 'alice'"));
    }

    [Fact]
    public void Masks_numeric_literals()
    {
        Assert.Equal("SELECT * FROM t WHERE id = ? AND age = ?", SqlRedactor.Mask("SELECT * FROM t WHERE id = 42 AND age = 3.5"));
    }

    [Fact]
    public void Keeps_keywords_and_identifiers()
    {
        Assert.Equal("SELECT * FROM users", SqlRedactor.Mask("SELECT * FROM users"));
    }

    [Fact]
    public void Masks_multiple_literals()
    {
        var masked = SqlRedactor.Mask("INSERT INTO t (a, b) VALUES ('x', 5)");
        Assert.Equal("INSERT INTO t (a, b) VALUES ('?', ?)", masked);
    }

    [Fact]
    public void Empty_input_returns_empty()
    {
        Assert.Equal(string.Empty, SqlRedactor.Mask(string.Empty));
    }
}
