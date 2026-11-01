using LitSSHmcp.Core.Services.Scheduling;
using Xunit;

namespace LitSSHmcp.Tests;

public class CrontabParserTests
{
    [Fact]
    public void Parses_user_crontab_and_skips_comments_and_environment()
    {
        const string text = """
# comment
MAILTO=ops@example.com
*/5 * * * * /usr/local/bin/check.sh --all
0 3 * * * /usr/bin/backup.sh >/var/log/backup.log 2>&1
""";

        var entries = CrontabParser.Parse(text, hasUserField: false, "deploy");

        Assert.Equal(2, entries.Count);
        Assert.Equal("*/5 * * * *", entries[0].Expression);
        Assert.Equal("/usr/local/bin/check.sh --all", entries[0].Command);
        Assert.Equal("deploy", entries[0].User);
        Assert.Equal(2, entries[0].LineIndex);
        Assert.Contains("backup.sh", entries[1].Command);
    }

    [Fact]
    public void Parses_system_crontab_user_field()
    {
        const string text = "0 3 * * * root /usr/local/bin/backup.sh --all\n";

        var entry = Assert.Single(CrontabParser.Parse(text, hasUserField: true, "root"));

        Assert.Equal("root", entry.User);
        Assert.Equal("/usr/local/bin/backup.sh --all", entry.Command);
        Assert.False(entry.IsPaused);
    }

    [Fact]
    public void Parses_paused_marker()
    {
        const string text = "#LITSSH_PAUSED# 0 3 * * * /usr/bin/backup.sh\n";

        var entry = Assert.Single(CrontabParser.Parse(text, hasUserField: false, "deploy"));

        Assert.True(entry.IsPaused);
        Assert.Equal("0 3 * * *", entry.Expression);
        Assert.Equal("/usr/bin/backup.sh", entry.Command);
    }

    [Fact]
    public void Toggles_pause_marker_and_keeps_other_lines()
    {
        const string text = "MAILTO=ops@example.com\n0 3 * * * /usr/bin/backup.sh\n";

        var paused = CrontabParser.SetPaused(text, 1, "0 3 * * * /usr/bin/backup.sh", paused: true);
        Assert.Equal("MAILTO=ops@example.com\n#LITSSH_PAUSED# 0 3 * * * /usr/bin/backup.sh\n", paused);

        var resumed = CrontabParser.SetPaused(paused, 1, "#LITSSH_PAUSED# 0 3 * * * /usr/bin/backup.sh", paused: false);
        Assert.Equal(text, resumed);
    }

    [Fact]
    public void Rejects_toggle_when_file_changed()
    {
        const string text = "0 3 * * * /usr/bin/backup.sh\n";

        var error = Assert.Throws<InvalidOperationException>(() =>
            CrontabParser.SetPaused(text, 0, "0 4 * * * /usr/bin/other.sh", paused: true));
        Assert.Contains("已变化", error.Message);
    }

    [Fact]
    public void Preserves_unescaped_percent_for_display()
    {
        const string text = "0 3 * * * date +\\%Y-\\%m-\\%d > /tmp/date.txt\n";

        var entry = Assert.Single(CrontabParser.Parse(text, hasUserField: false, "deploy"));

        Assert.Contains("%Y", entry.Command);
    }
}
