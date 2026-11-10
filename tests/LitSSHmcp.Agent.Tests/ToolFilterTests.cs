using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class ToolFilterTests
{
    [Theory]
    [InlineData("ssh_list_servers", "ssh")]
    [InlineData("ssh_snapshot_get", "ssh")]
    [InlineData("ssh_execute_command", "command")]
    [InlineData("ssh_execute_sudo", "command")]
    [InlineData("ssh_upload", "fileTransfer")]
    [InlineData("ssh_download", "fileTransfer")]
    [InlineData("ssh_list_files", "fileTransfer")]
    [InlineData("mysql_query", "mysql")]
    [InlineData("postgres_query", "postgres")]
    [InlineData("redis_read", "redis")]
    [InlineData("docker_query", "docker")]
    [InlineData("service_query", "service")]
    [InlineData("log_query", "log")]
    [InlineData("java_diagnostics", "java")]
    [InlineData("topology_query", "topology")]
    [InlineData("app_health_snapshot", "app")]
    [InlineData("mcp_usage_guide", "guide")]
    [InlineData("mcp_approval_round", "guide")]
    [InlineData("unknown_tool", null)]
    public void GroupOf_maps_tool_names(string tool, string? expected)
    {
        Assert.Equal(expected, ToolFilter.GroupOf(tool));
    }

    [Theory]
    [InlineData("ssh_snapshot_get", true, true, true)]
    [InlineData("ssh_snapshot_refresh", false, true, true)]
    [InlineData("topology_discover", false, true, true)]
    [InlineData("ssh_snapshot_refresh", false, false, false)]   // 外部 MCP 不放行同名工具
    [InlineData("ssh_execute_command", false, true, false)]
    [InlineData("service_restart", false, true, false)]
    public void IsReadOnlyAllowed_only_exempts_builtin_local_cache_probes(
        string name, bool readOnlyHint, bool allowLocalCacheProbes, bool expected)
    {
        Assert.Equal(expected,
            ToolFilter.IsReadOnlyAllowed(name, readOnlyHint, allowLocalCacheProbes));
    }
}
