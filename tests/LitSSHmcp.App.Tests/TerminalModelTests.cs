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
    public void Application_cursor_keys_use_ss3_sequences()
    {
        Assert.Equal("\u001bOA", TerminalView.CursorSequence('A', 1, applicationCursorKeys: true));
        Assert.Equal("\u001b[A", TerminalView.CursorSequence('A', 1, applicationCursorKeys: false));
        Assert.Equal("\u001b[1;2A", TerminalView.CursorSequence('A', 2, applicationCursorKeys: true));
    }

    [Fact]
    public void Kitty_keyboard_sequences_do_not_render_literal_text()
    {
        var model = new TerminalModel(30, 5);
        model.Feed("root$ ");
        model.Feed("\u001b[<1u\u001b[<u\u001b[>7u\u001b[=1u");

        Assert.Equal("root$", model.LastNonEmptyLineText());
        Assert.False(model.IsAltScreen);
    }

    [Fact]
    public void Esc_charset_selection_does_not_render_the_B_byte()
    {
        var model = new TerminalModel(30, 3);
        model.Feed("A\u001b(B\u001b[mB");

        Assert.Equal("AB", model.LastNonEmptyLineText());
        Assert.Equal('A', model.CellAt(0, 0).Ch);
        Assert.Equal('B', model.CellAt(1, 0).Ch);
    }

    [Fact]
    public void Alt_screen_exit_restores_main_screen_and_cursor()
    {
        var model = new TerminalModel(30, 5);
        model.Feed("main-prompt\r\n$ ");
        var savedX = model.CursorX;
        var savedY = model.CursorY;

        model.Feed("\u001b[?1049h");
        model.Feed("codex-ui");
        Assert.True(model.IsAltScreen);
        model.Feed("\u001b[?1049l");

        Assert.False(model.IsAltScreen);
        Assert.Equal(savedX, model.CursorX);
        Assert.Equal(savedY, model.CursorY);
        Assert.Contains("$", model.LastNonEmptyLineText());
        Assert.DoesNotContain("codex-ui", model.LastNonEmptyLineText());
    }

    [Fact]
    public void Emoji_is_rendered_as_one_wide_grapheme()
    {
        var model = new TerminalModel(20, 3);
        model.Feed("A😀B");

        Assert.Equal("A", model.CellAt(0, 0).Text);
        Assert.Equal("😀", model.CellAt(1, 0).Text);
        Assert.True(model.CellAt(2, 0).WideContinuation);
        Assert.Equal("B", model.CellAt(3, 0).Text);
        Assert.Equal(4, model.CursorX);
    }

    [Fact]
    public void Combining_mark_attaches_to_previous_cell()
    {
        var model = new TerminalModel(20, 3);
        model.Feed("e\u0301");

        Assert.Equal("e\u0301", model.CellAt(0, 0).Text);
        Assert.Equal(1, model.CursorX);
    }

    [Fact]
    public void Mouse_focus_and_sync_modes_are_tracked()
    {
        var model = new TerminalModel(20, 3);
        model.Feed("\u001b[?1002h\u001b[?1006h\u001b[?1004h\u001b[?2026h");
        Assert.Equal(MouseTrackingMode.Button, model.MouseTracking);
        Assert.True(model.MouseSgr);
        Assert.True(model.FocusReporting);
        Assert.True(model.SynchronizedOutput);

        model.Feed("\u001b[?1002l\u001b[?1006l\u001b[?1004l\u001b[?2026l");
        Assert.Equal(MouseTrackingMode.None, model.MouseTracking);
        Assert.False(model.MouseSgr);
        Assert.False(model.FocusReporting);
        Assert.False(model.SynchronizedOutput);
    }

    [Fact]
    public void Osc8_hyperlink_and_osc52_clipboard_are_parsed()
    {
        var model = new TerminalModel(30, 3);
        string? clipboard = null;
        model.ClipboardRequested += value => clipboard = value;
        model.Feed("\u001b]8;;https://example.com\u0007X\u001b]8;;\u0007Y");
        Assert.Equal("https://example.com", model.CellAt(0, 0).Hyperlink);
        Assert.Equal("X", model.CellAt(0, 0).Text);
        Assert.Null(model.CellAt(1, 0).Hyperlink);
        Assert.Equal("Y", model.CellAt(1, 0).Text);

        model.Feed("\u001b]52;c;SGVsbG8=\u0007");
        Assert.Equal("Hello", clipboard);
    }

    [Fact]
    public void Resize_reflows_wrapped_lines_and_restores_when_widened()
    {
        var model = new TerminalModel(10, 5);
        model.Feed("abcdefghijklm");

        model.Resize(5, 5);

        var dump = string.Join("|", Enumerable.Range(0, model.Rows).Select(r => RowText(model, r)));
        Assert.True(RowText(model, 0).Contains("abcde", StringComparison.Ordinal), dump);
        Assert.Contains("fghij", RowText(model, 1));
        Assert.Contains("klm", RowText(model, 2));

        model.Resize(10, 5);

        Assert.Contains("abcdefghij", RowText(model, 0));
        Assert.Contains("klm", RowText(model, 1));
    }

    [Fact]
    public void Resize_reflows_scrollback_content()
    {
        var model = new TerminalModel(12, 3);
        for (var i = 0; i < 6; i++)
            model.Feed($"line{i}-abcdefghij\r\n");

        var before = model.ScrollbackCount;
        Assert.True(before > 0);

        model.Resize(6, 3);

        Assert.True(model.ScrollbackCount > 0);
        var text = string.Join("\n", Enumerable.Range(1, model.ScrollbackCount)
            .Select(i => new string(model.ScrollbackLine(i).Select(c => c.Ch).ToArray())));
        Assert.Contains("line", text);
    }

    [Fact]
    public void Resize_while_alt_screen_still_reflows_main_screen_for_after_exit()
    {
        var model = new TerminalModel(10, 5);
        model.Feed("abcdefghijklm");
        model.Feed("\u001b[?1049h");

        model.Resize(5, 5);
        model.Feed("\u001b[?1049l");

        Assert.Contains("abcde", RowText(model, 0));
        Assert.Contains("fghij", RowText(model, 1));
        Assert.Contains("klm", RowText(model, 2));
    }

    [Fact]
    public void Resize_does_not_join_separate_lines()
    {
        var model = new TerminalModel(10, 5);
        model.Feed("abc\r\ndef");

        model.Resize(4, 5);

        Assert.Contains("abc", RowText(model, 0));
        Assert.Contains("def", RowText(model, 1));
    }

    private static string RowText(TerminalModel model, int row)
    {
        var sb = new System.Text.StringBuilder();
        for (var x = 0; x < model.Cols; x++)
            sb.Append(model.CellAt(x, row).Ch);
        return sb.ToString().TrimEnd();
    }

    [Fact]
    public void Application_cursor_keys_mode_toggles_with_decckm()
    {
        var model = new TerminalModel(20, 5);
        model.Feed("\u001b[?1h");
        Assert.True(model.ApplicationCursorKeys);
        model.Feed("\u001b[?1l");
        Assert.False(model.ApplicationCursorKeys);
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
        Assert.Equal('\0', model.CellAt(0, 0).Ch);
        Assert.Equal(" ", model.CellAt(0, 0).Text);

        model.Feed("XY\u001b[2J");
        Assert.Equal('\0', model.CellAt(0, 0).Ch);
        Assert.Equal('\0', model.CellAt(1, 0).Ch);
    }

    [Fact]
    public void Wide_characters_advance_two_columns()
    {
        var model = new TerminalModel(20, 5);
        model.Feed("中A");
        Assert.Equal('中', model.CellAt(0, 0).Ch);
        Assert.False(model.CellAt(0, 0).WideContinuation);
        Assert.True(model.CellAt(1, 0).WideContinuation);
        Assert.Equal('A', model.CellAt(2, 0).Ch);
        Assert.Equal(3, model.CursorX);
    }

    [Fact]
    public void Large_line_delete_does_not_throw_or_underflow()
    {
        var model = new TerminalModel(10, 4);
        model.Feed("A\r\nB\r\nC");
        model.Feed("\u001b[999M");
        Assert.True(model.Rows > 0);
    }

    [Fact]
    public void Responds_to_cursor_and_color_queries()
    {
        var model = new TerminalModel(20, 5);
        var responses = new List<string>();
        model.ResponseRequested += responses.Add;

        model.Feed("\u001b[2;3H\u001b[6n\u001b]11;?\u0007");

        Assert.Contains("\u001b[2;3R", responses);
        Assert.Contains(responses, r => r.StartsWith("\u001b]11;rgb:", StringComparison.Ordinal));
    }

    [Fact]
    public void Debian_vim_startup_sequence_renders_file_content()
    {
        var model = new TerminalModel(80, 24);
        model.Feed("\u001b[?2004h$ vim file.txt\r\n");
        model.Feed("\u001b[?1006;1000h\u001b[?1002h\u001b[?1049h\u001b[22;0;0t" +
                   "\u001b[>4;2m\u001b[?1h\u001b=\u001b[?2004h\u001b[?1004h\u001b[1;24r" +
                   "\u001b[?12h\u001b[?12l\u001b[22;2t\u001b[22;1t\u001b[27m\u001b[23m" +
                   "\u001b[29m\u001b[m\u001b[H\u001b[2J\u001b[?25l\u001b[24;1H\"file.txt\"" +
                   "\u001b[2;1H\u001b[6n\u001b[3;1H\u001bPzz\u001b\\\u001b[0%m\u001b[6n" +
                   "\u001b[1;1H\u001b[>c\u001b[38;144H1,1\u001b[11CAll\u001b[1;1H\u6D4B\u8BD5\u5185\u5BB9\r\n" +
                   "\u001b[94m~\u001b[m\u001b[24;1H\u001b[?25h");

        Assert.True(model.IsAltScreen);
        Assert.Equal('\u6D4B', model.CellAt(0, 0).Ch);
        Assert.Equal('\u8BD5', model.CellAt(2, 0).Ch);
        Assert.True(model.CellAt(1, 0).WideContinuation);
    }

    [Fact]
    public void Cursor_positioning_cancels_pending_wrap_so_first_line_is_not_scrolled_away()
    {
        // 真实 vim 场景：逐行画 "~" 时把光标顶到最后一列（触发 pending wrap），
        // 随后用 CUP 定位到状态行继续写。若 CUP 不清 pending wrap，
        // 接下来的字符会先在最后一行触发一次滚屏，导致首行内容被永久挤出屏幕。
        var model = new TerminalModel(10, 4);
        model.Feed("TOP\r\n");
        model.Feed("\u001b[4;1H0123456789");   // 最后一行写满 10 列 -> pending wrap
        Assert.Equal(9, model.CursorX);
        Assert.Equal(3, model.CursorY);

        model.Feed("\u001b[4;4HX");            // CUP 定位后写字符：不得换行/滚屏
        Assert.Equal('T', model.CellAt(0, 0).Ch);
        Assert.Equal('O', model.CellAt(1, 0).Ch);
        Assert.Equal('P', model.CellAt(2, 0).Ch);
        Assert.Equal('X', model.CellAt(3, 3).Ch);
        Assert.Equal(0, model.ScrollbackCount);
    }

    [Fact]
    public void Dcs_sequences_are_consumed_until_ST_and_not_rendered()
    {
        // vim 启动会发 DCS 探测（如 XTGETTCAP）：ESC P <payload> ESC \，
        // payload 必须整体吞掉，不能当普通字符画到屏幕上。
        var model = new TerminalModel(20, 5);
        model.Feed("\u001bP+q544e\u001b\\OK");
        Assert.Equal('O', model.CellAt(0, 0).Ch);
        Assert.Equal('K', model.CellAt(1, 0).Ch);
        Assert.Equal(2, model.CursorX);

        // ST 内的 ESC 不终止序列；连续 ESC 后跟 '\' 才终止。
        model.Feed("\u001bPq\u001b[0m\u001b\\tail");
        Assert.Equal('t', model.CellAt(2, 0).Ch);
        Assert.Equal('l', model.CellAt(5, 0).Ch);
    }

    [Fact]
    public void Device_attribute_queries_return_correct_DA1_and_DA2_responses()
    {
        var model = new TerminalModel(20, 5);
        var responses = new List<string>();
        model.ResponseRequested += responses.Add;

        model.Feed("\u001b[c");   // DA1：无前缀
        Assert.Contains("\u001b[?1;2c", responses);

        model.Feed("\u001b[>c");  // DA2：'>' 前缀
        Assert.Contains("\u001b[>0;95;0c", responses);
    }

    [Fact]
    public void Scrolling_pushes_lines_to_scrollback()
    {
        var model = new TerminalModel(10, 2);
        model.Feed("A\r\nB\r\nC");
        Assert.Equal(1, model.ScrollbackCount);
        Assert.Equal('A', model.ScrollbackLine(1)[0].Ch);
    }

    [Fact]
    public void Scrollback_line_is_padded_after_resize()
    {
        var model = new TerminalModel(5, 2);
        model.Feed("A\r\nB\r\nC");
        model.Resize(20, 2);
        var line = model.ScrollbackLine(1);
        Assert.Equal(20, line.Length);
    }

    [Fact]
    public void Bracketed_paste_mode_tracks_remote_switch()
    {
        var model = new TerminalModel(20, 3);
        model.Feed("\u001b[?2004h");
        Assert.True(model.BracketedPasteEnabled);
        model.Feed("\u001b[?2004l");
        Assert.False(model.BracketedPasteEnabled);
    }

    [Fact]
    public void Last_line_text_reflects_prompt()
    {
        var model = new TerminalModel(20, 3);
        model.Feed("hello\r\nuser@host:~$ ");
        Assert.EndsWith("$", model.LastNonEmptyLineText());
    }

    [Fact]
    public void Osc133_tracks_command_state_and_exit_code()
    {
        var model = new TerminalModel(20, 3);
        model.Feed("\u001b]133;C\u0007");
        Assert.True(model.IsCommandRunning);

        model.Feed("\u001b]133;D;7\u0007");
        Assert.False(model.IsCommandRunning);
        Assert.Equal(7, model.LastExitCode);

        model.Feed("\u001b]133;A\u0007");
        Assert.Equal(7, model.LastExitCode);
    }

    [Fact]
    public void Osc7_tracks_working_directory()
    {
        var model = new TerminalModel(20, 3);
        model.Feed("\u001b]7;file://host/home/user\u0007");
        Assert.Equal("/home/user", model.CurrentDirectory);
    }

    [Fact]
    public void Osc_title_with_non_ascii_is_not_rendered()
    {
        // 服务器把中文主机名写进 OSC 0/2 标题时，标题内容必须整体吞掉，
        // 不能按 grapheme 画到光标处（会在提示符前留下乱码）。
        var model = new TerminalModel(20, 3);
        model.Feed("\u001b]0;我的主机\u0007$ ");

        Assert.Equal('$', model.CellAt(0, 0).Ch);
        Assert.Equal(' ', model.CellAt(1, 0).Ch);
        Assert.Equal(2, model.CursorX);
    }

    [Fact]
    public void Dcs_payload_with_non_ascii_is_not_rendered()
    {
        var model = new TerminalModel(20, 3);
        model.Feed("\u001bP我的负载\u001b\\AB");

        Assert.Equal('A', model.CellAt(0, 0).Ch);
        Assert.Equal('B', model.CellAt(1, 0).Ch);
        Assert.Equal(2, model.CursorX);
    }

    [Fact]
    public void Cursor_style_sequence_is_consumed_without_rendering_q()
    {
        // DECSCUSR：CSI Ps SP q（zsh/fish 切换 vi 模式时常发），终结字节 q 不能显示。
        var model = new TerminalModel(20, 3);
        model.Feed("AB\u001b[2 qCD");

        Assert.Equal("ABCD", model.LastNonEmptyLineText());
        Assert.Equal(4, model.CursorX);
    }

    [Fact]
    public void Mode_query_is_answered_without_rendering()
    {
        var model = new TerminalModel(20, 3);
        var responses = new List<string>();
        model.ResponseRequested += responses.Add;

        model.Feed("\u001b[?2026$p");
        model.Feed("\u001b[?2026h");
        model.Feed("\u001b[?2026$p");

        Assert.Contains("\u001b[?2026;2$y", responses);
        Assert.Contains("\u001b[?2026;1$y", responses);
        Assert.Equal(0, model.CursorX);
        Assert.Equal("", model.LastNonEmptyLineText());
    }

    [Fact]
    public void Overwriting_half_of_wide_char_clears_the_pair()
    {
        var model = new TerminalModel(10, 2);
        model.Feed("中");
        Assert.True(model.CellAt(1, 0).WideContinuation);

        model.Feed("\rA");

        Assert.Equal('A', model.CellAt(0, 0).Ch);
        Assert.False(model.CellAt(1, 0).WideContinuation);
        Assert.Equal('\0', model.CellAt(1, 0).Ch);
    }

    [Fact]
    public void Deleting_one_cell_does_not_leave_orphan_wide_continuation()
    {
        var model = new TerminalModel(10, 2);
        model.Feed("A中B");
        model.Feed("\u001b[2G\u001b[1P");

        Assert.Equal('A', model.CellAt(0, 0).Ch);
        Assert.False(model.CellAt(1, 0).WideContinuation);
        Assert.Equal('\0', model.CellAt(1, 0).Ch);
        Assert.Equal('B', model.CellAt(2, 0).Ch);
    }

    [Fact]
    public void Insert_mode_shifts_existing_text()
    {
        var model = new TerminalModel(10, 2);
        model.Feed("AB");
        model.Feed("\u001b[2G\u001b[4hX");

        Assert.Equal('A', model.CellAt(0, 0).Ch);
        Assert.Equal('X', model.CellAt(1, 0).Ch);
        Assert.Equal('B', model.CellAt(2, 0).Ch);

        // 关闭插入模式后恢复覆盖行为。
        model.Feed("\u001b[4l\u001b[1GY");
        Assert.Equal('Y', model.CellAt(0, 0).Ch);
        Assert.Equal('X', model.CellAt(1, 0).Ch);
    }

    [Fact]
    public void Auto_wrap_mode_change_clears_pending_wrap()
    {
        var model = new TerminalModel(5, 3);
        model.Feed("12345");
        Assert.Equal(4, model.CursorX);

        model.Feed("\u001b[?7lX");

        Assert.Equal('X', model.CellAt(4, 0).Ch);
        Assert.Equal(0, model.CursorY);
        Assert.Equal(0, model.ScrollbackCount);
    }

    [Fact]
    public void Soft_wrap_flag_survives_lf_and_is_cleared_by_erase()
    {
        var model = new TerminalModel(5, 3);
        model.Feed("abcdefg");
        Assert.True(model.IsScreenRowWrapped(0));
        Assert.False(model.IsScreenRowWrapped(1));

        // readline/zsh 重绘时会用 \n 在软换行的两行之间移动，不能因此丢掉标记。
        model.Feed("\r\n");
        Assert.True(model.IsScreenRowWrapped(0));

        model.Feed("\u001b[1;1H\u001b[2K");
        Assert.False(model.IsScreenRowWrapped(0));
    }

    [Fact]
    public void Repeat_last_char_sequence_writes_the_character_n_times()
    {
        var model = new TerminalModel(20, 3);
        model.Feed("A\u001b[4bB");

        Assert.Equal('B', model.CellAt(5, 0).Ch);
        Assert.Equal("AAAAAB", model.LastNonEmptyLineText());
    }

    [Fact]
    public void Decstr_resets_modes_but_keeps_screen()
    {
        var model = new TerminalModel(20, 3);
        var responses = new List<string>();
        model.ResponseRequested += responses.Add;
        model.Feed("keep\u001b[4h\u001b[!p\u001b[4$p");

        Assert.Contains("\u001b[4;2$y", responses);
        Assert.Equal('k', model.CellAt(0, 0).Ch);
    }
}
