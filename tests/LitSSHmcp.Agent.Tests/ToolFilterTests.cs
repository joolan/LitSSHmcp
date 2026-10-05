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
    [InlineData("ssh_upload_file", "fileTransfer")]
    [InlineData("ssh_upload_files", "fileTransfer")]
    [InlineData("ssh_download_file", "fileTransfer")]
    [InlineData("ssh_download_files", "fileTransfer")]
    [InlineData("ssh_list_files", "fileTransfer")]
    [InlineData("mysql_query", "mysql")]
    [InlineData("postgres_diagnostics", "postgres")]
    [InlineData("redis_read", "redis")]
    [InlineData("docker_ps", "docker")]
    [InlineData("service_status", "service")]
    [InlineData("log_tail", "log")]
    [InlineData("java_threads", "java")]
    [InlineData("topology_get_overview", "topology")]
    [InlineData("app_health_snapshot", "app")]
    [InlineData("mcp_usage_guide", "guide")]
    [InlineData("unknown_tool", null)]
    public void GroupOf_maps_tool_names(string tool, string? expected)
    {
        Assert.Equal(expected, ToolFilter.GroupOf(tool));
    }
}
