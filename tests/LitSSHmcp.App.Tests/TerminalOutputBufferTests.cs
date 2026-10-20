using LitSSHmcp.App.Services.LocalTerminal;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class TerminalOutputBufferTests
{
    [Fact]
    public void Read_returns_only_output_after_cursor()
    {
        var buffer = new TerminalOutputBuffer();
        buffer.Append("first\n");
        var first = buffer.Read(0, 8000);
        Assert.Equal("first\n", first.Text);
        Assert.False(first.Truncated);

        buffer.Append("second\n");
        var second = buffer.Read(first.NextCursor, 8000);
        Assert.Equal("second\n", second.Text);
        Assert.Equal(first.NextCursor + "second\n".Length, second.NextCursor);
    }

    [Fact]
    public void Read_pages_large_output_without_losing_middle()
    {
        var buffer = new TerminalOutputBuffer();
        buffer.Append(new string('a', 5000));
        buffer.Append(new string('b', 5000));

        var first = buffer.Read(0, 4000);
        Assert.Equal(new string('a', 4000), first.Text);
        Assert.Equal(4000, first.NextCursor);
        Assert.True(first.Truncated);

        var second = buffer.Read(first.NextCursor, 4000);
        Assert.Equal(new string('a', 1000) + new string('b', 3000), second.Text);
        Assert.Equal(8000, second.NextCursor);
        Assert.True(second.Truncated);

        var third = buffer.Read(second.NextCursor, 4000);
        Assert.Equal(new string('b', 2000), third.Text);
        Assert.Equal(10000, third.NextCursor);
        Assert.False(third.Truncated);
    }

    [Fact]
    public void Read_marks_truncated_after_ring_eviction()
    {
        var buffer = new TerminalOutputBuffer(4096);
        buffer.Append(new string('a', 3000));
        buffer.Append(new string('b', 3000));

        var result = buffer.Read(0, 8000);

        Assert.True(result.Truncated);
        Assert.DoesNotContain(new string('a', 3000), result.Text);
        Assert.Contains(new string('b', 3000), result.Text);
    }

    [Fact]
    public void Clean_removes_ansi_and_internal_markers()
    {
        var id = Guid.NewGuid().ToString("N");
        var text = "\u001B[31mred\u001B[0m\n<<<LITSSH_" + id + ">>>|0|C:\\work\nnext\r\n";

        var cleaned = TerminalOutputBuffer.Clean(text);

        Assert.False(cleaned.Contains('\u001B'), $"codes: {string.Join(",", cleaned.Select(c => (int)c))}");
        Assert.DoesNotContain("LITSSH_", cleaned);
        Assert.Contains("red", cleaned);
        Assert.Contains("next", cleaned);
        Assert.DoesNotContain("\r", cleaned);
    }
}
