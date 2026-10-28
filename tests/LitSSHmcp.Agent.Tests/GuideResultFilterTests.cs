using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class GuideResultFilterTests
{
    private static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        "ssh_snapshot_get", "ssh_snapshot_refresh", "ssh_execute_command",
        "mysql_query", "mysql_execute"
    };

    private static HashSet<string> Allowed(params string[] names) =>
        new(names, StringComparer.Ordinal);

    [Fact]
    public void Removes_routing_lines_that_reference_disallowed_tools()
    {
        const string json = """
            {"title":"x","server_query_guide":{"when_to_use":[
              "用 ssh_snapshot_get 看快照",
              "用 ssh_execute_command 执行命令",
              "通用说明"
            ]}}
            """;

        var filtered = GuideResultFilter.Filter(json, All, Allowed("ssh_snapshot_get"))!;

        Assert.Contains("ssh_snapshot_get", filtered);
        Assert.DoesNotContain("ssh_execute_command", filtered);
        Assert.Contains("通用说明", filtered);
    }

    [Fact]
    public void Filters_error_handling_clauses_individually()
    {
        const string json = """
            {"error_handling":"blocked=xxx; snapshot_not_found=用 ssh_snapshot_refresh; readonly_statement=改用只读工具"}
            """;

        var filtered = GuideResultFilter.Filter(json, All, Allowed("mysql_query"))!;

        Assert.DoesNotContain("ssh_snapshot_refresh", filtered);
        Assert.Contains("readonly_statement", filtered);
        Assert.Contains("blocked", filtered);
    }

    [Fact]
    public void Filters_tools_array()
    {
        const string json = """
            {"tools":[{"name":"mysql_query"},{"name":"ssh_execute_command"}]}
            """;

        var filtered = GuideResultFilter.Filter(json, All, Allowed("mysql_query"))!;

        Assert.Contains("mysql_query", filtered);
        Assert.DoesNotContain("ssh_execute_command", filtered);
    }

    [Fact]
    public void NonJson_returns_unchanged()
    {
        const string text = "not json";

        Assert.Equal(text, GuideResultFilter.Filter(text, All, Allowed()));
    }
}
