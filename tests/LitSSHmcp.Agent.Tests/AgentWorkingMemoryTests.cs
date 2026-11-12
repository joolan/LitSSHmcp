using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class AgentWorkingMemoryTests
{
    [Fact]
    public void Builds_goal_plan_facts_and_open_items()
    {
        var memory = new AgentWorkingMemory();
        memory.SetGoal("巡检 web 服务器");
        memory.SetPlan(new[] { new PlanItem(false, "查磁盘"), new PlanItem(true, "查端口") });
        memory.RecordToolResult("ssh_snapshot_get", true,
            """{"status":"ok","data":{"sections":{"resource":{},"portmap":{}}}}""");
        memory.RecordToolResult("docker_query", false, "timeout");

        var text = memory.BuildContext();

        Assert.NotNull(text);
        Assert.Contains("巡检 web 服务器", text);
        Assert.Contains("[ ] 查磁盘", text);
        Assert.Contains("[x] 查端口", text);
        Assert.Contains("sections=resource,portmap", text);
        Assert.Contains("docker_query", text);
        Assert.Contains("未完成/阻塞", text);
    }

    [Fact]
    public void New_goal_clears_previous_task_facts_but_continuation_keeps_them()
    {
        var memory = new AgentWorkingMemory();
        memory.SetGoal("巡检 web 服务器");
        memory.RecordToolResult("ssh_snapshot_get", true, "{\"status\":\"ok\"}");

        memory.SetGoal("继续排查");
        Assert.Contains("已确认事实", memory.BuildContext());

        memory.SetGoal("换一台数据库服务器做慢查询分析");
        Assert.DoesNotContain("已确认事实", memory.BuildContext());
    }
    [Fact]
    public void Export_and_import_roundtrip()
    {
        var memory = new AgentWorkingMemory();
        memory.SetGoal("巡检");
        memory.SetPlan(new[] { new PlanItem(false, "查磁盘") });
        memory.RecordToolResult("ssh_snapshot_get", true, "{\"status\":\"ok\"}");

        var restored = new AgentWorkingMemory();
        restored.ImportJson(memory.ExportJson());

        var text = restored.BuildContext();
        Assert.Contains("巡检", text);
        Assert.Contains("查磁盘", text);
        Assert.Contains("ssh_snapshot_get", text);
    }
}
