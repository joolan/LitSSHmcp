using LitSSHmcp.Agent;
using LitSSHmcp.App.ViewModels;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class IncidentInsightEntryTests
{
    [Fact]
    public void From_builds_display_fields()
    {
        var record = new IncidentRecord(7, "磁盘满恢复", "df 100%", "ssh:web01", "journald",
            "清理日志", "恢复", "raw:1", 0.8, "resolved", "agent-1", "2026-10-26", "2026-10-26", null);

        var entry = IncidentInsightEntry.From(record);

        Assert.Equal("[resolved] 磁盘满恢复", entry.DisplayTitle);
        Assert.Contains("#7", entry.Meta);
        Assert.Contains("ssh:web01", entry.Meta);
        Assert.Equal("journald", entry.Preview);
    }
}
