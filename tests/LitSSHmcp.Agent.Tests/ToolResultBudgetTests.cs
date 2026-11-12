using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class ToolResultBudgetTests
{
    [Fact]
    public void Resolves_per_tool_budgets()
    {
        Assert.Equal(ToolResultBudget.SnapshotChars, ToolResultBudget.Resolve("ssh_snapshot_get", 4000));
        Assert.Equal(ToolResultBudget.LogChars, ToolResultBudget.Resolve("log_query", 4000));
        Assert.Equal(ToolResultBudget.FleetChars, ToolResultBudget.Resolve("fleet_query", 4000));
        Assert.Equal(4000, ToolResultBudget.Resolve("ssh_execute_command", 4000));
    }

    [Fact]
    public void Snapshot_summary_keeps_sections_not_raw_payload()
    {
        var json = """{"status":"ok","data":{"sections":{"resource":{"status":"ok","dataChars":12,"data":{"blob":"___"}},"portmap":{"status":"degraded","dataChars":34}}}}""";
        var summary = ToolResultSummaryBuilder.Build("ssh_snapshot_get", json, 500);

        Assert.Contains("快照摘要", summary);
        Assert.Contains("resource(ok,12)", summary);
        Assert.Contains("portmap(degraded,34)", summary);
        Assert.DoesNotContain("___", summary);
    }

    [Fact]
    public void Log_summary_prefers_tail()
    {
        var text = string.Join('\n', Enumerable.Range(1, 100).Select(i => "line" + i));
        var summary = ToolResultSummaryBuilder.Build("log_query", text, 500);

        Assert.Contains("最新", summary);
        Assert.Contains("line100", summary);
        Assert.DoesNotContain("line1\n", summary);
    }
}
