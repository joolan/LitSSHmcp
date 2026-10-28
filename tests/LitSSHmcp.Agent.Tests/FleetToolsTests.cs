using LitSSHmcp.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class FleetToolsTests
{
    [Fact]
    public async Task Fans_out_readonly_tool_and_merges_results()
    {
        var tool = new FakeTool("ssh_snapshot_get", readOnly: true, result: "{\"status\":\"ok\"}");
        var fleet = FleetTools.Create(new[] { tool });
        Assert.NotNull(fleet);

        var result = (await fleet!.InvokeAsync(new Dictionary<string, object?>
        {
            ["tool"] = "ssh_snapshot_get",
            ["targets"] = "s1, s2",
            ["argumentsJson"] = """{"section":"resource","detail":"summary"}"""
        }, default))?.ToString() ?? string.Empty;

        Assert.Equal(2, tool.Calls.Count);
        Assert.Contains(tool.Calls, c => Equals(c["serverId"], "s1") && c["section"]?.ToString() == "resource");
        Assert.Contains(tool.Calls, c => Equals(c["serverId"], "s2"));
        Assert.Contains("[OK] s1", result);
        Assert.Contains("[OK] s2", result);
        Assert.Contains("成功 2", result);
        Assert.Contains("失败 0", result);
    }

    [Fact]
    public async Task Rejects_write_or_unknown_tool()
    {
        var write = new FakeTool("ssh_execute_command", readOnly: false);
        Assert.Null(FleetTools.Create(new[] { write }));

        var readonlyTool = new FakeTool("ssh_snapshot_get");
        var fleet = FleetTools.Create(new[] { readonlyTool });
        Assert.NotNull(fleet);

        var result = (await fleet!.InvokeAsync(new Dictionary<string, object?>
        {
            ["tool"] = "ssh_execute_command",
            ["targets"] = "s1"
        }, default))?.ToString() ?? string.Empty;

        Assert.Contains("不支持或不可用", result);
        Assert.Empty(write.Calls);
    }

    [Fact]
    public async Task Respects_denied_callback()
    {
        var tool = new FakeTool("ssh_snapshot_get");
        var fleet = FleetTools.Create(new[] { tool }, name => name == "ssh_snapshot_get");

        var result = (await fleet!.InvokeAsync(new Dictionary<string, object?>
        {
            ["tool"] = "ssh_snapshot_get",
            ["targets"] = "s1,s2"
        }, default))?.ToString() ?? string.Empty;

        Assert.Empty(tool.Calls);
        Assert.Contains("已被会话策略禁用", result);
    }

    [Fact]
    public async Task Caps_targets_and_reports_truncation()
    {
        var tool = new FakeTool("ssh_snapshot_get");
        var fleet = FleetTools.Create(new[] { tool });

        var result = (await fleet!.InvokeAsync(new Dictionary<string, object?>
        {
            ["tool"] = "ssh_snapshot_get",
            ["targets"] = "s1,s2,s3,s4,s5",
            ["maxTargets"] = 2
        }, default))?.ToString() ?? string.Empty;

        Assert.Equal(2, tool.Calls.Count);
        Assert.Contains("只执行前 2", result);
    }

    [Fact]
    public async Task Invalid_arguments_json_returns_error()
    {
        var tool = new FakeTool("ssh_snapshot_get");
        var fleet = FleetTools.Create(new[] { tool });

        var result = (await fleet!.InvokeAsync(new Dictionary<string, object?>
        {
            ["tool"] = "ssh_snapshot_get",
            ["targets"] = "s1",
            ["argumentsJson"] = "{not-json}"
        }, default))?.ToString() ?? string.Empty;

        Assert.Contains("不是合法 JSON", result);
        Assert.Empty(tool.Calls);
    }

    private sealed class FakeTool(string name, bool readOnly = true, object? result = null) : IAgentTool
    {
        public List<IDictionary<string, object?>> Calls { get; } = new();
        public string Name { get; } = name;
        public AITool Tool => null!;
        public bool Destructive => false;
        public bool ReadOnly { get; } = readOnly;
        public string? ToolGroup => null;

        public Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
        {
            Calls.Add(arguments ?? new Dictionary<string, object?>());
            return Task.FromResult<object?>(result ?? "{}");
        }
    }
}
