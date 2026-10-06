using LitSSHmcp.App.Controls;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>终端缓冲/解析器的基础行为测试（自绘终端）。</summary>
public class TerminalModelTests
{
    [Fact]
    public void Prints_text_and_advances_cursor()
    {
        var model = new TerminalModel(20, 5);
        model.Feed("ABC");
        Assert.Equal('A', model.CellAt(0, 0).Ch);
        Assert.Equal('B', model.CellAt(1, 0).Ch);
        Assert.Equal('C', model.CellAt(2, 0).Ch);
        Assert.Equal(3, model.CursorX);
        Assert.Equal(0, model.CursorY);
    }

    [Fact]
    public void Carriage_return_and_line_feed()
    {
        var model = new TerminalModel(20, 5);
        model.Feed("AB\r\nCD");
        Assert.Equal('A', model.CellAt(0, 0).Ch);
        Assert.Equal('C', model.CellAt(0, 1).Ch);
        Assert.Equal('D', model.CellAt(1, 1).Ch);
        Assert.Equal(2, model.CursorX);
        Assert.Equal(1, model.CursorY);
    }

    [Fact]
    public void Cursor_position_is_one_based()
    {
        var model = new TerminalModel(20, 5);
        model.Feed("\u001b[2;3HX");
        Assert.Equal('X', model.CellAt(2, 1).Ch);
    }

    [Fact]
    public void Sgr_sets_color_and_reset_clears()
    {
        var model = new TerminalModel(20, 5);
        model.Feed("\u001b[31mA");
        Assert.Equal(0xCD0000u, model.CellAt(0, 0).Attr.Fg);

        model.Feed("\u001b[0mB");
        Assert.Null(model.CellAt(1, 0).Attr.Fg);
    }

    [Fact]
    public void Alternate_screen_restores_main()
    {
        var model = new TerminalModel(20, 5);
        model.Feed("MAIN");
        model.Feed("\u001b[?1049h");
        model.Feed("ALT");
        Assert.Equal('A', model.CellAt(0, 0).Ch);

        model.Feed("\u001b[?1049l");
        Assert.Equal('M', model.CellAt(0, 0).Ch);
        Assert.Equal('A', model.CellAt(1, 0).Ch);
        Assert.Equal('I', model.CellAt(2, 0).Ch);
    }

    [Fact]
    public void Erase_in_line_and_display()
    {
        var model = new TerminalModel(10, 3);
        model.Feed("ABCDEF\u001b[2K");
        Assert.Equal(' ', model.CellAt(0, 0).Ch);

        model.Feed("XY\u001b[2J");
        Assert.Equal(' ', model.CellAt(0, 0).Ch);
        Assert.Equal(' ', model.CellAt(1, 0).Ch);
    }

    [Fact]
    public void Scrolling_pushes_lines_to_scrollback()
    {
        var model = new TerminalModel(10, 2);
        model.Feed("A\r\nB\r\nC");
        Assert.Equal(1, model.ScrollbackCount);
        Assert.Equal('A', model.ScrollbackLine(1)[0].Ch);
    }
}
