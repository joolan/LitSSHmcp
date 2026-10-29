using System.Text.Json;
using System.Text.RegularExpressions;
using LitSSHmcp.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class FleetChangeToolsTests
{
    [Fact]
    public async Task Plan_dry_run_canary_apply_and_rollback_flow()
    {
        var exec = new FakeFleetExecTool();
        var test = new FakeTestConnectionTool();
        var tool = FleetChangeTools.Create(exec, test)!;

        var planText = await Invoke(tool, ("mode", "plan"), ("serverIds", "s1,s2"),
            ("command", "systemctl restart nginx"), ("rollbackCommand", "systemctl restart nginx-old"));
        Assert.Contains("批量变更计划", planText);
        var planId = ExtractPlanId(planText);

        var applyTooEarly = await Invoke(tool, ("mode", "apply"), ("planId", planId), ("confirm", true));
        Assert.Contains("必须先 canary", applyTooEarly);

        var canaryBeforeDryRun = await Invoke(tool, ("mode", "canary"), ("planId", planId), ("confirm", true));
        Assert.Contains("必须先完成 dry_run", canaryBeforeDryRun);

        var dryRun = await Invoke(tool, ("mode", "dry_run"), ("planId", planId));
        Assert.Contains("预检通过", dryRun);
        Assert.Equal(2, test.Calls);

        var canary = await Invoke(tool, ("mode", "canary"), ("planId", planId), ("confirm", true));
        Assert.Contains("canary 成功", canary);
        Assert.Single(exec.Calls);
        Assert.Equal("s1", exec.Calls[0].Targets);

        var apply = await Invoke(tool, ("mode", "apply"), ("planId", planId), ("confirm", true));
        Assert.Contains("apply 完成", apply);
        Assert.Equal(2, exec.Calls.Count);
        Assert.Equal("s2", exec.Calls[1].Targets);

        var rollback = await Invoke(tool, ("mode", "rollback"), ("planId", planId), ("confirm", true));
        Assert.Contains("rollback 完成", rollback);
        Assert.Equal(3, exec.Calls.Count);
        Assert.Contains("s1", exec.Calls[2].Targets);
        Assert.Contains("s2", exec.Calls[2].Targets);
        Assert.Equal("systemctl restart nginx-old", exec.Calls[2].Command);
    }

    [Fact]
    public async Task Plan_normalizes_asset_id_prefixes()
    {
        var tool = FleetChangeTools.Create(new FakeFleetExecTool(), new FakeTestConnectionTool())!;

        var text = await Invoke(tool, ("mode", "plan"), ("serverIds", "ssh:s1, ssh://s2"),
            ("command", "systemctl restart nginx"));

        Assert.Contains("- s1", text);
        Assert.Contains("- s2", text);
        Assert.DoesNotContain("- ssh:s1", text);
        Assert.DoesNotContain("- ssh://s2", text);
    }

    [Fact]
    public async Task Plan_rejects_script_files()
    {
        var tool = FleetChangeTools.Create(new FakeFleetExecTool(), new FakeTestConnectionTool())!;

        var text = await Invoke(tool, ("mode", "plan"), ("serverIds", "s1"),
            ("command", "bash deploy.sh"));

        Assert.Contains("不支持脚本文件", text);
    }

    [Fact]
    public async Task Apply_requires_rollback_command()
    {
        var exec = new FakeFleetExecTool();
        var tool = FleetChangeTools.Create(exec, new FakeTestConnectionTool())!;
        var planText = await Invoke(tool, ("mode", "plan"), ("serverIds", "s1"),
            ("command", "systemctl restart nginx"));
        var planId = ExtractPlanId(planText);
        await Invoke(tool, ("mode", "dry_run"), ("planId", planId));
        var canary = await Invoke(tool, ("mode", "canary"), ("planId", planId), ("confirm", true));
        Assert.Contains("canary 成功", canary);

        var apply = await Invoke(tool, ("mode", "apply"), ("planId", planId), ("confirm", true));
        Assert.Contains("必须提供 rollbackCommand", apply);
    }

    [Fact]
    public async Task Confirm_is_required_for_write_phases()
    {
        var exec = new FakeFleetExecTool();
        var tool = FleetChangeTools.Create(exec, new FakeTestConnectionTool())!;
        var planText = await Invoke(tool, ("mode", "plan"), ("serverIds", "s1"),
            ("command", "systemctl restart nginx"), ("rollbackCommand", "true"));
        var planId = ExtractPlanId(planText);
        await Invoke(tool, ("mode", "dry_run"), ("planId", planId));

        var canary = await Invoke(tool, ("mode", "canary"), ("planId", planId));
        Assert.Contains("confirm=true", canary);
        Assert.Empty(exec.Calls);
    }

    [Fact]
    public async Task Write_phases_reject_when_hidden_fleet_exec_is_denied()
    {
        var exec = new FakeFleetExecTool();
        var tool = FleetChangeTools.Create(exec, new FakeTestConnectionTool(), new MemoryFleetPlanStore(),
            "alice", 1, isDenied: name => name == "ssh_execute_fleet")!;
        var planText = await Invoke(tool, ("mode", "plan"), ("serverIds", "s1"),
            ("command", "systemctl restart nginx"), ("rollbackCommand", "true"));
        var planId = ExtractPlanId(planText);
        await Invoke(tool, ("mode", "dry_run"), ("planId", planId));

        var canary = await Invoke(tool, ("mode", "canary"), ("planId", planId), ("confirm", true));

        Assert.Contains("已被当前会话策略禁用", canary);
        Assert.Empty(exec.Calls);
    }

    [Fact]
    public async Task Cancelled_canary_marks_plan_cancelled()
    {
        var exec = new BlockingFleetExecTool();
        var store = new MemoryFleetPlanStore();
        var tool = FleetChangeTools.Create(exec, new FakeTestConnectionTool(), store)!;
        var planText = await Invoke(tool, ("mode", "plan"), ("serverIds", "s1"),
            ("command", "systemctl restart nginx"), ("rollbackCommand", "true"));
        var planId = ExtractPlanId(planText);
        await Invoke(tool, ("mode", "dry_run"), ("planId", planId));
        using var cts = new CancellationTokenSource();

        var running = tool.InvokeAsync(new Dictionary<string, object?>
        {
            ["mode"] = "canary", ["planId"] = planId, ["confirm"] = true
        }, cts.Token);
        await exec.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        var text = (await running)?.ToString() ?? string.Empty;

        Assert.Contains("已取消计划", text);
        var plan = await store.GetAsync(planId, "local", null);
        Assert.NotNull(plan);
        Assert.Equal("cancelled", plan!.Phase);
        Assert.False(plan.Running);
        Assert.Equal("用户取消", plan.LastError);
    }

    [Fact]
    public async Task Plan_timeout_and_selective_rollback_only_touch_requested_target()
    {
        var exec = new FakeFleetExecTool();
        var tool = FleetChangeTools.Create(exec, new FakeTestConnectionTool())!;
        var planText = await Invoke(tool, ("mode", "plan"), ("serverIds", "s1,s2"),
            ("command", "systemctl restart nginx"), ("rollbackCommand", "true"), ("timeoutSeconds", 120));
        var planId = ExtractPlanId(planText);
        await Invoke(tool, ("mode", "dry_run"), ("planId", planId));
        await Invoke(tool, ("mode", "canary"), ("planId", planId), ("confirm", true));
        await Invoke(tool, ("mode", "apply"), ("planId", planId), ("confirm", true));

        var rollback = await Invoke(tool, ("mode", "rollback"), ("planId", planId),
            ("serverIds", "s2"), ("confirm", true));

        Assert.Contains("rollback 完成", rollback);
        Assert.Equal("s2", exec.Calls[^1].Targets);
        Assert.All(exec.Timeouts, t => Assert.Equal(120, t));
        var status = await Invoke(tool, ("mode", "status"), ("planId", planId));
        Assert.Contains("已变更=1", status);
    }

    [Fact]
    public async Task Retry_failed_retries_only_failed_canary_target()
    {
        var exec = new FailOnceFleetExecTool();
        var tool = FleetChangeTools.Create(exec, new FakeTestConnectionTool())!;
        var planText = await Invoke(tool, ("mode", "plan"), ("serverIds", "s1,s2"),
            ("command", "systemctl restart nginx"), ("rollbackCommand", "true"));
        var planId = ExtractPlanId(planText);
        await Invoke(tool, ("mode", "dry_run"), ("planId", planId));

        var canary = await Invoke(tool, ("mode", "canary"), ("planId", planId), ("confirm", true));
        Assert.Contains("canary 失败", canary);
        var retry = await Invoke(tool, ("mode", "retry_failed"), ("planId", planId), ("confirm", true));

        Assert.Contains("retry_failed 完成", retry);
        Assert.Equal(2, exec.Calls.Count);
        Assert.Equal("s1", exec.Calls[1].Targets);
    }

    private sealed class BlockingFleetExecTool : IAgentTool
    {
        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "ssh_execute_fleet";
        public AITool Tool => null!;
        public bool Destructive => true;
        public bool ReadOnly => false;
        public string? ToolGroup => "command";

        public async Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
        {
            Entered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
    }

    private sealed class FailOnceFleetExecTool : IAgentTool
    {
        public sealed record Call(string Targets, string Command);
        public List<Call> Calls { get; } = new();
        public List<int> Timeouts { get; } = new();
        public string Name => "ssh_execute_fleet";
        public AITool Tool => null!;
        public bool Destructive => true;
        public bool ReadOnly => false;
        public string? ToolGroup => "command";

        public Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
        {
            var targets = arguments?["serverIds"]?.ToString() ?? string.Empty;
            Calls.Add(new Call(targets, arguments?["command"]?.ToString() ?? string.Empty));
            var fail = Calls.Count == 1;
            var items = targets.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => $"{{\"serverId\":\"{id}\",\"success\":{(fail ? "false" : "true")}," +
                             $"\"error\":\"{(fail ? "boom" : "")}\"}}");
            var json = $$"""{"success":{{(fail ? "false" : "true")}},"summary":"{{(fail ? "boom" : "ok")}}","items":[{{string.Join(",", items)}}]}""";
            return Task.FromResult<object?>(JsonDocument.Parse(json).RootElement.Clone());
        }
    }

    private static string ExtractPlanId(string text) =>
        Regex.Match(text, "planId=\"([a-f0-9]+)\"").Groups[1].Value;

    private static async Task<string> Invoke(IAgentTool tool, params (string Key, object? Value)[] args)
    {
        var dict = args.ToDictionary(x => x.Key, x => x.Value);
        return (await tool.InvokeAsync(dict, default))?.ToString() ?? string.Empty;
    }

    private sealed class FakeFleetExecTool : IAgentTool
    {
        public sealed record Call(string Targets, string Command);
        public List<Call> Calls { get; } = new();
        public List<int> Timeouts { get; } = new();
        public string Name => "ssh_execute_fleet";
        public AITool Tool => null!;
        public bool Destructive => true;
        public bool ReadOnly => false;
        public string? ToolGroup => "command";

        public Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
        {
            var targets = arguments?["serverIds"]?.ToString() ?? string.Empty;
            var command = arguments?["command"]?.ToString() ?? string.Empty;
            Calls.Add(new Call(targets, command));
            Timeouts.Add(Convert.ToInt32(arguments?["timeoutSeconds"] ?? 60));
            var items = targets.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => $"{{\"serverId\":\"{id}\",\"success\":true}}");
            var json = $$"""{"success":true,"summary":"ok","items":[{{string.Join(",", items)}}]}""";
            return Task.FromResult<object?>(JsonDocument.Parse(json).RootElement.Clone());
        }
    }

    private sealed class FakeTestConnectionTool : IAgentTool
    {
        public int Calls { get; private set; }
        public string Name => "ssh_test_connection";
        public AITool Tool => null!;
        public bool Destructive => false;
        public bool ReadOnly => true;
        public string? ToolGroup => "ssh";

        public Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<object?>(JsonDocument.Parse("""{"success":true}""").RootElement.Clone());
        }
    }
}
