using System.Text;
using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

/// <summary>第二轮强健性审计第二批（H5 路由过滤 / H6 摘要按节压缩 / QW12 退避）的回归测试。</summary>
public class RobustnessBatch2Tests
{
    // —— H5：路由表按实际可用工具过滤，只读 profile 不再教模型调写工具 ——

    /// <summary>只读 profile（诊断/资产/问答）下实际暴露的 MCP 工具名（全部只读或安全探测）。</summary>
    private static readonly string[] ReadOnlyToolNames =
    [
        "ssh_list_servers", "ssh_test_connection", "ssh_snapshot_get", "ssh_snapshot_refresh", "ssh_snapshot_diff",
        "job_start", "job_status", "job_result", "job_events", "job_list",
        "log_query", "ssh_list_files", "ssh_download",
        "service_query", "docker_query", "java_processes", "java_diagnostics",
        "datasource_list", "datasource_test_connection",
        "mysql_query", "postgres_query", "redis_read",
        "topology_query", "topology_discover", "topology_history",
        "app_health_snapshot", "mcp_usage_guide", "mcp_self_check"
    ];

    private static string BuildRouting(IReadOnlyCollection<string>? availableToolNames)
    {
        var sb = new StringBuilder();
        SystemPromptBuilder.AppendToolRouting(sb, null, subAgent: false, fleetTools: false,
            fleetChangeTools: false, availableToolNames: availableToolNames);
        return sb.ToString();
    }

    [Fact]
    public void Routing_hides_write_only_routes_in_read_only_mode()
    {
        var routing = BuildRouting(ReadOnlyToolNames);

        // 命令执行路由（ssh_execute_command + ssh_execute_sudo 均为写工具）应整行剔除
        Assert.DoesNotContain("ssh_execute_sudo", routing);
        Assert.DoesNotContain("命令执行 / Shell / sudo", routing);
    }

    [Fact]
    public void Routing_annotates_partially_available_routes_in_read_only_mode()
    {
        var routing = BuildRouting(ReadOnlyToolNames);

        // 混合路由应保留只读工具并标注写工具不可用
        Assert.Contains("mysql_query", routing);
        Assert.Contains("mysql_execute 不可用", routing);
        Assert.Contains("redis_execute 不可用", routing);
        Assert.Contains("service_restart 不可用", routing);
        Assert.Contains("docker_restart / docker_exec 不可用", routing);
        Assert.Contains("ssh_upload 不可用", routing);
        Assert.Contains("job_cancel 不可用", routing);
    }

    [Fact]
    public void Routing_keeps_all_routes_when_tool_names_not_filtered()
    {
        var routing = BuildRouting(null);

        Assert.Contains("ssh_execute_sudo", routing);
        Assert.DoesNotContain("不可用", routing);
    }

    // —— H6：滚动摘要超预算按节压缩，"必须保留"节永不丢 ——

    [Fact]
    public void CompactSummary_preserves_must_keep_sections_when_over_budget()
    {
        var blocks = new List<string>
        {
            // 第一轮摘要（最早，含必须保留）
            "目标：排查 web01 500 错误\n当前状态：nginx 502\n必须保留：部署路径 /opt/webapp/current，凭据线索 ~/.deploy-key\n尚未解决：无",
            // 中间填充大量低价值轮次，撑爆 4000 字预算
        };
        for (var i = 0; i < 80; i++)
            blocks.Add($"目标：第{i}轮检查\n当前状态：常规巡检无异常，各项指标平稳，磁盘占用正常，负载波动不大，网络延迟符合预期，缓存命中率处于合理区间，无需人工介入处理\n出现错误：无\n尚未解决：无");

        var summary = string.Join("\n", blocks);
        Assert.True(summary.Length > AgentSession.MaxSummaryChars, "测试前置：摘要应超过预算");

        var compacted = AgentSession.CompactSummary(summary);

        Assert.True(compacted.Length <= AgentSession.MaxSummaryChars, $"压缩后仍超预算: {compacted.Length}");
        // 最早的"必须保留"节不能被砍头截断丢掉
        Assert.Contains("必须保留", compacted);
        Assert.Contains("/opt/webapp/current", compacted);
        Assert.Contains("凭据线索 ~/.deploy-key", compacted);
    }

    [Fact]
    public void CompactSummary_keeps_recent_sections_over_old_ones()
    {
        var blocks = new List<string> { "目标：最早的目标\n当前状态：旧状态\n必须保留：保留项A" };
        for (var i = 0; i < 80; i++)
            blocks.Add($"目标：第{i}轮目标\n当前状态：第{i}轮状态，巡检过程记录了较多中间输出用于填充摘要体积，确保整体长度超过压缩预算触发按节裁剪，本段为纯填充文本无实际信息量\n尚未解决：第{i}轮待办");
        blocks.Add("目标：最新目标\n当前状态：最新状态\n尚未解决：最新待办");

        var compacted = AgentSession.CompactSummary(string.Join("\n", blocks));

        Assert.Contains("保留项A", compacted);
        Assert.Contains("最新待办", compacted);
        Assert.DoesNotContain("最早的目标", compacted);
    }

    // —— QW12：指数退避 + Retry-After ——

    [Fact]
    public void RetryDelay_uses_exponential_backoff_with_jitter()
    {
        // attempt=1: 300ms ± 15%
        Assert.InRange(ResilientChatClient.RetryDelay(null, 1).TotalMilliseconds, 255, 345);
        // attempt=4: 300*8=2400ms ± 15%
        Assert.InRange(ResilientChatClient.RetryDelay(null, 4).TotalMilliseconds, 2040, 2760);
    }

    [Fact]
    public void RetryDelay_caps_at_eight_seconds()
    {
        var delay = ResilientChatClient.RetryDelay(null, 30);
        Assert.InRange(delay.TotalMilliseconds, 8000 * 0.85, 8000 * 1.15);
    }
}
