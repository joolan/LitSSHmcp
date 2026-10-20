using LitSSHmcp.App.Services;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class LogTerminalCommandBuilderTests
{
    [Fact]
    public void TailLatest_quotes_path_and_clamps_lines()
    {
        var command = LogTerminalCommandBuilder.TailLatest("/var/log/app's.log", 999999);

        Assert.StartsWith("tail -n 5000 -- ", command);
        Assert.Contains("'/var/log/app'\\''s.log'", command);
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(-5, 200)]
    [InlineData(1, 1)]
    [InlineData(5000, 5000)]
    [InlineData(5001, 5000)]
    public void ClampLines_bounds_result(int input, int expected)
    {
        Assert.Equal(expected, LogTerminalCommandBuilder.ClampLines(input));
    }

    [Fact]
    public void Follow_starts_from_now_and_survives_rotation()
    {
        Assert.Equal("tail -n 0 -F -- '/var/log/app.log'", LogTerminalCommandBuilder.Follow("/var/log/app.log"));
    }

    [Fact]
    public void FollowFiltered_pipes_through_line_buffered_grep()
    {
        var command = LogTerminalCommandBuilder.FollowFiltered("/var/log/app.log", "OutOfMemory|GC overhead");

        Assert.Contains("tail -n 0 -F -- '/var/log/app.log'", command);
        Assert.Contains("grep --line-buffered -E -i -- 'OutOfMemory|GC overhead'", command);
    }

    [Fact]
    public void FollowFiltered_without_pattern_falls_back_to_plain_follow()
    {
        Assert.Equal(
            LogTerminalCommandBuilder.Follow("/var/log/app.log"),
            LogTerminalCommandBuilder.FollowFiltered("/var/log/app.log", "  "));
    }

    [Fact]
    public void SearchLastHour_uses_remote_date_and_iso_time_filter()
    {
        var command = LogTerminalCommandBuilder.SearchLastHour("/var/log/app.log", "Exception", 300);

        Assert.Contains("date -d '1 hour ago'", command);
        Assert.Contains("awk -v s=", command);
        Assert.Contains("grep -E -i -- 'Exception'", command);
        Assert.EndsWith("tail -n 300", command);
        Assert.Contains("tail -n 200000", command);
        Assert.Contains("/dev/stderr", command);
    }

    [Fact]
    public void SearchLastHour_without_keyword_only_filters_by_time()
    {
        var command = LogTerminalCommandBuilder.SearchLastHour("/var/log/app.log", null);

        Assert.DoesNotContain("grep", command);
        Assert.EndsWith("tail -n 200", command);
    }

    [Fact]
    public void SearchBetween_quotes_time_bounds_and_keyword()
    {
        var command = LogTerminalCommandBuilder.SearchBetween(
            "/var/log/app.log", "2026-10-20 10:00:00", "2026-10-20 11:00:00", "error");

        Assert.Contains("awk -v s='2026-10-20 10:00:00' -v e='2026-10-20 11:00:00'", command);
        Assert.Contains("grep -E -i -- 'error'", command);
        Assert.EndsWith("tail -n 200", command);
    }

    [Fact]
    public void Errors_filters_common_failure_keywords()
    {
        var command = LogTerminalCommandBuilder.Errors("/var/log/app.log");

        Assert.StartsWith("tail -n 2000 -- '/var/log/app.log'", command);
        Assert.Contains("grep -n -E -i --", command);
        Assert.Contains("exception", command);
        Assert.Contains("outofmemory", command);
        Assert.EndsWith("tail -n 200", command);
    }

    [Fact]
    public void GrepContext_uses_bounded_context_and_max_lines()
    {
        var command = LogTerminalCommandBuilder.GrepContext("/var/log/app.log", "timeout", 5, 100);

        Assert.Contains("grep -n -E -i -C 5 -- 'timeout' '/var/log/app.log'", command);
        Assert.EndsWith("tail -n 100", command);
    }

    [Fact]
    public void Count_quotes_pattern_and_path()
    {
        var command = LogTerminalCommandBuilder.Count("/var/log/app.log", "error|warn");

        Assert.Equal("grep -c -E -i -- 'error|warn' '/var/log/app.log'", command);
    }

    [Fact]
    public void LessAtEnd_jumps_to_tail_and_quotes_path()
    {
        Assert.Equal("less +G -- '/var/log/app.log'", LogTerminalCommandBuilder.LessAtEnd("/var/log/app.log"));
    }

    [Fact]
    public void SliceToFile_scans_whole_file_with_keyword_directly()
    {
        var command = LogTerminalCommandBuilder.SliceToFile(
            "/var/log/nginx/access.log", "/var/log/nginx/access.slice.log", "/api/order");

        Assert.StartsWith("grep -E -i -- '/api/order' '/var/log/nginx/access.log'", command);
        Assert.Contains("> '/var/log/nginx/access.slice.log'", command);
        Assert.EndsWith("wc -l '/var/log/nginx/access.slice.log'; ls -lh '/var/log/nginx/access.slice.log'", command);
    }

    [Fact]
    public void SliceToFile_without_pattern_copies_whole_file()
    {
        var command = LogTerminalCommandBuilder.SliceToFile("/var/log/a.log", "/var/log/a.slice.log");

        Assert.StartsWith("cat -- '/var/log/a.log' > '/var/log/a.slice.log'", command);
    }

    [Fact]
    public void SliceToFile_with_scan_limit_uses_tail()
    {
        var command = LogTerminalCommandBuilder.SliceToFile(
            "/var/log/a.log", "/var/log/a.slice.log", "error", maxScanLines: 1000);

        Assert.StartsWith("tail -n 1000 -- '/var/log/a.log' | grep -E -i -- 'error'", command);
    }

    [Fact]
    public void SliceToFile_with_time_filter_uses_nginx_aware_awk()
    {
        var command = LogTerminalCommandBuilder.SliceToFile(
            "/var/log/nginx/access.log", "/var/log/nginx/a.slice.log", "/api",
            maxScanLines: 0, since: "2026-10-20 10:00:00", until: "2026-10-20 11:00:00");

        Assert.Contains("cat -- '/var/log/nginx/access.log' | awk -v s='2026-10-20 10:00:00'", command);
        Assert.Contains("Jan Feb Mar", command);
        Assert.Contains("match($0,", command);
        Assert.Contains("gsub(/\\//", command);
        Assert.Contains("| grep -E -i -- '/api'", command);
    }

    [Fact]
    public void SliceToFile_rejects_output_equal_to_source()
    {
        Assert.Throws<ArgumentException>(() =>
            LogTerminalCommandBuilder.SliceToFile("/var/log/a.log", "/var/log/a.log"));
    }

    [Fact]
    public void Newlines_in_patterns_and_paths_are_flattened()
    {
        var command = LogTerminalCommandBuilder.FollowFiltered("/var/log/a\nb.log", "error\r\nboom");

        Assert.DoesNotContain('\n', command);
        Assert.DoesNotContain('\r', command);
        Assert.Contains("'/var/log/a b.log'", command);
        Assert.Contains("'error  boom'", command);
    }

    [Fact]
    public void All_commands_are_single_line_for_terminal_injection()
    {
        var path = "/var/log/app.log";
        var commands = new[]
        {
            LogTerminalCommandBuilder.TailLatest(path),
            LogTerminalCommandBuilder.Follow(path),
            LogTerminalCommandBuilder.FollowFiltered(path, "error"),
            LogTerminalCommandBuilder.SearchLastHour(path, "error"),
            LogTerminalCommandBuilder.SearchBetween(path, "2026-10-20 10:00:00", "2026-10-20 11:00:00", "error"),
            LogTerminalCommandBuilder.Errors(path),
            LogTerminalCommandBuilder.GrepContext(path, "error"),
            LogTerminalCommandBuilder.Count(path, "error"),
            LogTerminalCommandBuilder.LessAtEnd(path),
            LogTerminalCommandBuilder.SliceToFile(path, "/tmp/out.log", "error")
        };

        Assert.All(commands, command =>
        {
            Assert.DoesNotContain('\n', command);
            Assert.DoesNotContain('\r', command);
        });
    }
}
