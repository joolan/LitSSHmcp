using LitSSHmcp.Core.Services.Datasource;
using Xunit;

namespace LitSSHmcp.Tests;

public class RedisCommandParserTests
{
    [Fact]
    public void Splits_simple_command()
    {
        Assert.Equal(new[] { "GET", "user:1" }, RedisCommandParser.Split("GET user:1"));
    }

    [Fact]
    public void Collapses_extra_whitespace()
    {
        Assert.Equal(new[] { "SLOWLOG", "GET", "5" }, RedisCommandParser.Split("  SLOWLOG   GET  5  "));
    }

    [Fact]
    public void Keeps_double_quoted_value_with_spaces()
    {
        Assert.Equal(new[] { "SET", "k", "hello world" }, RedisCommandParser.Split("SET k \"hello world\""));
    }

    [Fact]
    public void Keeps_single_quoted_value_with_spaces()
    {
        Assert.Equal(new[] { "SET", "k", "a b" }, RedisCommandParser.Split("SET k 'a b'"));
    }

    [Fact]
    public void Supports_backslash_escape_outside_quotes()
    {
        Assert.Equal(new[] { "SET", "k", "a b" }, RedisCommandParser.Split(@"SET k a\ b"));
    }

    [Fact]
    public void Supports_escaped_quote_inside_double_quotes()
    {
        Assert.Equal(new[] { "SET", "k", "say\"hi" }, RedisCommandParser.Split("SET k \"say\\\"hi\""));
    }

    [Fact]
    public void Joins_adjacent_quoted_and_plain_parts()
    {
        Assert.Equal(new[] { "SET", "k", "ab" }, RedisCommandParser.Split("SET k a\"b\""));
    }

    [Fact]
    public void Throws_on_empty_command()
    {
        Assert.Throws<FormatException>(() => RedisCommandParser.Split("   "));
        Assert.Throws<FormatException>(() => RedisCommandParser.Split(null));
    }

    [Fact]
    public void Throws_on_unterminated_quote()
    {
        Assert.Throws<FormatException>(() => RedisCommandParser.Split("SET k \"oops"));
    }
}
