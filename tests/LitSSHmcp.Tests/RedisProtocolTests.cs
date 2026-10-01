using LitSSHmcp.Core.Services.Datasource;
using Xunit;

namespace LitSSHmcp.Tests;

public class RedisProtocolTests
{
    [Fact]
    public void Encodes_command_as_resp_array_of_bulk_strings()
    {
        var bytes = RespCodec.EncodeCommand(new[] { "SET", "k", "v" });
        Assert.Equal("*3\r\n$3\r\nSET\r\n$1\r\nk\r\n$1\r\nv\r\n", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Encodes_multibyte_arguments_with_byte_length()
    {
        var bytes = RespCodec.EncodeCommand(new[] { "SET", "k", "张三" });
        // 张三 = 6 UTF-8 字节
        Assert.Equal("*3\r\n$3\r\nSET\r\n$1\r\nk\r\n$6\r\n张三\r\n", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Parses_simple_string()
    {
        var value = await ReadAsync("+OK\r\n");
        Assert.Equal(RedisValueKind.SimpleString, value.Kind);
        Assert.Equal("OK", value.Text);
    }

    [Fact]
    public async Task Parses_error()
    {
        var value = await ReadAsync("-ERR unknown command\r\n");
        Assert.True(value.IsError);
        Assert.Equal("ERR unknown command", value.ErrorMessage);
    }

    [Fact]
    public async Task Parses_integer()
    {
        var value = await ReadAsync(":42\r\n");
        Assert.Equal(RedisValueKind.Integer, value.Kind);
        Assert.Equal(42, value.Integer);
    }

    [Fact]
    public async Task Parses_bulk_string()
    {
        var value = await ReadAsync("$5\r\nhello\r\n");
        Assert.Equal(RedisValueKind.BulkString, value.Kind);
        Assert.Equal("hello", value.Text);
    }

    [Fact]
    public async Task Parses_null_bulk_string()
    {
        var value = await ReadAsync("$-1\r\n");
        Assert.True(value.IsNull);
    }

    [Fact]
    public async Task Parses_null_array()
    {
        var value = await ReadAsync("*-1\r\n");
        Assert.True(value.IsNull);
    }

    [Fact]
    public async Task Parses_array_of_bulk_strings()
    {
        var value = await ReadAsync("*2\r\n$3\r\nfoo\r\n$3\r\nbar\r\n");
        Assert.Equal(RedisValueKind.Array, value.Kind);
        Assert.Equal(new[] { "foo", "bar" }, value.Items!.Select(i => i.Text));
    }

    [Fact]
    public async Task Parses_nested_array_with_integer()
    {
        var value = await ReadAsync("*2\r\n:1\r\n*1\r\n$1\r\nx\r\n");
        Assert.Equal(RedisValueKind.Array, value.Kind);
        Assert.Equal(RedisValueKind.Integer, value.Items![0].Kind);
        Assert.Equal(RedisValueKind.Array, value.Items![1].Kind);
        Assert.Equal("x", value.Items![1].Items![0].Text);
    }

    [Fact]
    public async Task Parses_utf8_bulk_string()
    {
        var value = await ReadAsync("$6\r\n张三\r\n");
        Assert.Equal("张三", value.Text);
    }

    [Fact]
    public async Task Throws_on_truncated_stream()
    {
        await Assert.ThrowsAsync<IOException>(() => ReadAsync("$5\r\nhel"));
    }

    [Fact]
    public async Task Throws_on_invalid_prefix()
    {
        await Assert.ThrowsAsync<IOException>(() => ReadAsync("#garbage\r\n"));
    }

    private static async Task<RedisValue> ReadAsync(string payload)
    {
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload));
        return await new RespReader(stream).ReadAsync();
    }
}

public class RedisValueFormatterTests
{
    [Fact]
    public void Truncates_arrays_beyond_max_items()
    {
        var items = Enumerable.Range(0, 10).Select(i => RedisValue.Bulk($"v{i}")).ToArray();
        var (value, truncated) = RedisValueFormatter.Format(RedisValue.Array(items), 3);

        var list = Assert.IsType<List<object?>>(value);
        Assert.Equal(3, list.Count);
        Assert.True(truncated);
    }

    [Fact]
    public void Truncates_long_strings()
    {
        var longText = new string('a', RedisValueFormatter.MaxStringLength + 100);
        var (value, truncated) = RedisValueFormatter.Format(RedisValue.Bulk(longText), 10);

        var text = Assert.IsType<string>(value);
        Assert.True(text.Length < longText.Length);
        Assert.Contains("已截断", text);
        Assert.True(truncated);
    }

    [Fact]
    public void Keeps_scalars_and_null()
    {
        Assert.Null(RedisValueFormatter.Format(RedisValue.Null, 10).Value);
        Assert.Equal(7L, RedisValueFormatter.Format(RedisValue.Number(7), 10).Value);
        Assert.Equal("ok", RedisValueFormatter.Format(RedisValue.Simple("ok"), 10).Value);
    }
}
