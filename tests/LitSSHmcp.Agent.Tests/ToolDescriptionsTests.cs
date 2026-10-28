using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class ToolDescriptionsTests
{
    [Fact]
    public void Short_description_is_returned_unchanged()
    {
        const string description = "在SSH服务器执行命令。写操作需审批";

        Assert.Equal(description, ToolDescriptions.Compact(description));
    }

    [Fact]
    public void Action_description_keeps_all_actions_and_exclusion_hint()
    {
        const string description =
            "MySQL 只读入口：action=query 执行 SELECT；action=explain 分析执行计划；action=diagnostics 做整体诊断。写 SQL 用 mysql_execute。";

        var compact = ToolDescriptions.Compact(description);

        Assert.Contains("action=explain", compact);
        Assert.Contains("action=diagnostics", compact);
        Assert.Contains("mysql_execute", compact);
    }

    [Fact]
    public void Service_query_description_keeps_restart_hint()
    {
        const string description =
            "统一查询 systemd：action=list 列服务单元（可 pattern 过滤）、action=status 看服务状态、action=logs 看 journal 日志；重启用 service_restart。";

        var compact = ToolDescriptions.Compact(description);

        Assert.Contains("service_restart", compact);
    }

    [Fact]
    public void Overlong_description_is_trimmed_and_points_to_usage_guide()
    {
        var description = new string('长', 300);

        var compact = ToolDescriptions.Compact(description);

        Assert.Contains("mcp_usage_guide", compact);
        Assert.True(compact.Length < description.Length);
    }
}
