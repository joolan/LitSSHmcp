using LitSSHmcp.Agent;
using LitSSHmcp.App.Services;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>桌面跨进程对账辅助（SessionAutoRefresh）的判定规则。</summary>
public class SessionAutoRefreshTests
{
    private static AgentSessionRow Row(long id, string updatedAt, string title = "t")
        => new(id, title, "2026-10-18T00:00:00Z", updatedAt);

    [Fact]
    public void Snapshot_empty_is_zero()
    {
        Assert.Equal("0", SessionAutoRefresh.Snapshot(null));
        Assert.Equal("0", SessionAutoRefresh.Snapshot(Array.Empty<AgentSessionRow>()));
    }

    [Fact]
    public void Snapshot_same_rows_are_stable()
    {
        var rows = new[] { Row(1, "2026-10-18T01:00:00Z"), Row(2, "2026-10-18T02:00:00Z") };
        Assert.Equal(SessionAutoRefresh.Snapshot(rows), SessionAutoRefresh.Snapshot(rows.ToArray()));
    }

    [Fact]
    public void Snapshot_changes_when_rows_added_or_updated()
    {
        var before = new[] { Row(1, "2026-10-18T01:00:00Z") };
        var added = new[] { Row(1, "2026-10-18T01:00:00Z"), Row(2, "2026-10-18T03:00:00Z") };
        var updated = new[] { Row(1, "2026-10-18T04:00:00Z") };

        Assert.NotEqual(SessionAutoRefresh.Snapshot(before), SessionAutoRefresh.Snapshot(added));
        Assert.NotEqual(SessionAutoRefresh.Snapshot(before), SessionAutoRefresh.Snapshot(updated));
    }

    [Fact]
    public void Should_reload_selected_only_when_updated_at_differs()
    {
        Assert.False(SessionAutoRefresh.ShouldReloadSelected("2026-10-18T01:00:00Z", "2026-10-18T01:00:00Z"));
        Assert.True(SessionAutoRefresh.ShouldReloadSelected("2026-10-18T01:00:00Z", "2026-10-18T02:00:00Z"));
        Assert.True(SessionAutoRefresh.ShouldReloadSelected(null, "2026-10-18T02:00:00Z"));
        Assert.False(SessionAutoRefresh.ShouldReloadSelected(null, null));
    }
}
