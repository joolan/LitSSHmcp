using LitSSHmcp.App.Controls;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>终端选中文本提取：软换行按逻辑行合并，真实换行保留。</summary>
public class TerminalSelectionTests
{
    [Fact]
    public void Wrapped_rows_are_joined_without_newline()
    {
        var model = new TerminalModel(6, 4);
        model.Feed("abcdefghij");

        var text = TerminalView.ExtractText(model, 0, 0, 1, 5, 0, 6);

        Assert.Equal("abcdefghij", text);
    }

    [Fact]
    public void Real_newlines_are_preserved()
    {
        var model = new TerminalModel(10, 4);
        model.Feed("line1\r\nline2");

        var text = TerminalView.ExtractText(model, 0, 0, 1, 4, 0, 10);

        Assert.Equal("line1\nline2", text);
    }

    [Fact]
    public void Wrapped_rows_stay_joined_after_readline_lf_move()
    {
        var model = new TerminalModel(6, 4);
        model.Feed("abcdefghij");
        // 重绘长行时 shell 会用 \n 在软换行的两行之间移动。
        model.Feed("\r\n");

        var text = TerminalView.ExtractText(model, 0, 0, 1, 5, 0, 6);

        Assert.Equal("abcdefghij", text);
    }

    [Fact]
    public void Selection_starting_mid_row_still_joins_wrap()
    {
        var model = new TerminalModel(6, 4);
        model.Feed("abcdefghij");

        var text = TerminalView.ExtractText(model, 0, 2, 1, 2, 0, 6);

        Assert.Equal("cdefghi", text);
    }

    [Fact]
    public void Trailing_spaces_are_trimmed_on_real_line_ends()
    {
        var model = new TerminalModel(10, 4);
        model.Feed("abc   \r\nx");

        var text = TerminalView.ExtractText(model, 0, 0, 1, 0, 0, 10);

        Assert.Equal("abc\nx", text);
    }

    [Fact]
    public void Wrapped_scrollback_rows_are_joined()
    {
        var model = new TerminalModel(6, 2);
        model.Feed("abcdefghij\r\nK");

        // 第一行已滚入回看区，滚动到顶部让长行的两段同时可见。
        var scrollback = model.ScrollbackCount;
        Assert.True(scrollback > 0);
        var text = TerminalView.ExtractText(model, 0, 0, 1, 5, scrollback, 6);

        Assert.Equal("abcdefghij", text);
    }
}
