using System.Text.Json;
using System.Text.RegularExpressions;
using LitSSHmcp.Agent;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class FleetPlanStoreTests
{
    [Fact]
    public async Task Plan_persists_across_runtime_instances_and_is_owner_scoped()
    {
        var db = TempDb();
        try
        {
            var store = new SqliteFleetPlanStore(db);
            var first = FleetChangeTools.Create(new FakeFleetExec(), new FakeTestConnection(), store,
                "alice", 7)!;
            var planText = await Invoke(first, ("mode", "plan"), ("serverIds", "s1"),
                ("command", "systemctl restart nginx"), ("rollbackCommand", "true"));
            var planId = Regex.Match(planText, @"planId=""([a-f0-9]+)""").Groups[1].Value;
            Assert.Contains("批量变更计划", planText);

            var second = FleetChangeTools.Create(new FakeFleetExec(), new FakeTestConnection(), store,
                "alice", 7)!;
            var status = await Invoke(second, ("mode", "status"), ("planId", planId));
            Assert.Contains("s1", status);
            Assert.Contains("阶段: planned", status);

            var otherOwner = FleetChangeTools.Create(new FakeFleetExec(), new FakeTestConnection(), store,
                "bob", 7)!;
            var denied = await Invoke(otherOwner, ("mode", "status"), ("planId", planId));
            Assert.Contains("不存在", denied);

            var otherSession = FleetChangeTools.Create(new FakeFleetExec(), new FakeTestConnection(), store,
                "alice", 8)!;
            var deniedSession = await Invoke(otherSession, ("mode", "status"), ("planId", planId));
            Assert.Contains("不存在", deniedSession);

            Assert.NotNull(await store.GetAsync(planId, "alice", 7));
        }
        finally
        {
            TryDelete(db);
        }
    }

    [Fact]
    public async Task Unsaved_session_plans_are_adopted_by_first_real_session()
    {
        var db = TempDb();
        try
        {
            var store = new SqliteFleetPlanStore(db);
            var unsaved = FleetChangeTools.Create(new FakeFleetExec(), new FakeTestConnection(), store,
                "local", null)!;
            var planText = await Invoke(unsaved, ("mode", "plan"), ("serverIds", "s1"),
                ("command", "systemctl restart nginx"), ("rollbackCommand", "true"));
            var planId = Regex.Match(planText, @"planId=""([a-f0-9]+)""").Groups[1].Value;
            Assert.NotNull(await store.GetAsync(planId, "local", null));

            await store.AdoptSessionAsync("local", "alice", 7);
            Assert.NotNull(await store.GetAsync(planId, "alice", 7));

            var saved = FleetChangeTools.Create(new FakeFleetExec(), new FakeTestConnection(), store,
                "alice", 7)!;
            var status = await Invoke(saved, ("mode", "status"), ("planId", planId));
            Assert.Contains("s1", status);
        }
        finally
        {
            TryDelete(db);
        }
    }

    [Fact]
    public async Task Plan_rejects_targets_outside_visible_server_list()
    {
        var list = new FakeListServers("s1", "s2");
        var tool = FleetChangeTools.Create(new FakeFleetExec(), new FakeTestConnection(),
            new MemoryFleetPlanStore(), "alice", 1, list)!;

        var text = await Invoke(tool, ("mode", "plan"), ("serverIds", "s1,s3"),
            ("command", "systemctl restart nginx"));

        Assert.Contains("s3", text);
        Assert.Contains("不存在或无权访问", text);
    }

    [Fact]
    public async Task Plan_rejects_targets_without_exec_permission()
    {
        var list = new FakeListServers("s1") { CanExec = false };
        var tool = FleetChangeTools.Create(new FakeFleetExec(), new FakeTestConnection(),
            new MemoryFleetPlanStore(), "alice", 1, list)!;

        var text = await Invoke(tool, ("mode", "plan"), ("serverIds", "s1"),
            ("command", "systemctl restart nginx"));

        Assert.Contains("缺少 exec 权限", text);
        Assert.Contains("s1", text);
    }

    [Fact]
    public async Task Stale_running_plan_is_marked_interrupted()
    {
        var store = new MemoryFleetPlanStore();
        var plan = new FleetPlanState
        {
            Id = "stale1",
            OwnerKey = "alice",
            SessionId = 1,
            Running = true,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-20)
        };
        await store.SaveAsync(plan);

        var loaded = await store.GetAsync("stale1", "alice", 1);

        Assert.NotNull(loaded);
        Assert.False(loaded!.Running);
        Assert.Equal("stale", loaded.Phase);
        Assert.Contains("中断", loaded.LastError);
    }

    [Fact]
    public async Task Progress_notifications_update_plan_target_status()
    {
        var store = new MemoryFleetPlanStore();
        var tool = FleetChangeTools.Create(new FakeFleetExec(progress: true), new FakeTestConnection(),
            store, "alice", 1)!;
        var planText = await Invoke(tool, ("mode", "plan"), ("serverIds", "s1"),
            ("command", "systemctl restart nginx"), ("rollbackCommand", "true"));
        var planId = Regex.Match(planText, @"planId=""([a-f0-9]+)""").Groups[1].Value;
        await Invoke(tool, ("mode", "dry_run"), ("planId", planId));
        await Invoke(tool, ("mode", "canary"), ("planId", planId), ("confirm", true));

        var plan = await store.GetAsync(planId, "alice", 1);
        Assert.NotNull(plan);
        var item = Assert.Single(plan!.TargetResults);
        Assert.Equal("s1", item.ServerId);
        Assert.Equal("ok", item.Status);
        Assert.Equal("canary", item.Phase);
    }

    [Fact]
    public async Task Fleet_targets_filters_visible_servers()
    {
        var tool = FleetTargetTools.Create(new FakeListServers("web01", "db01"))!;
        var json = (await tool.InvokeAsync(new Dictionary<string, object?> { ["query"] = "web" }, default))
            ?.ToString() ?? "";
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("web01", doc.RootElement.GetProperty("servers")[0].GetProperty("id").GetString());
    }

    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"litssh-fleet-{Guid.NewGuid():N}.db");

    private static void TryDelete(string db)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(db + suffix); } catch { /* 忽略 */ }
    }

    private static async Task<string> Invoke(IAgentTool tool, params (string Key, object? Value)[] args)
    {
        var dict = args.ToDictionary(x => x.Key, x => x.Value);
        return (await tool.InvokeAsync(dict, default))?.ToString() ?? string.Empty;
    }

    private sealed class FakeTestConnection : IAgentTool
    {
        public string Name => "ssh_test_connection";
        public AITool Tool => null!;
        public bool Destructive => false;
        public bool ReadOnly => true;
        public string? ToolGroup => "ssh";

        public Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct) =>
            Task.FromResult<object?>(JsonDocument.Parse("""{"success":true}""").RootElement.Clone());
    }

    private sealed class FakeListServers(params string[] ids) : IAgentTool
    {
        public bool? CanExec { get; init; }
        public string Name => "ssh_list_servers";
        public AITool Tool => null!;
        public bool Destructive => false;
        public bool ReadOnly => true;
        public string? ToolGroup => "ssh";

        public Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(new
            {
                success = true,
                servers = ids.Select(id => new
                {
                    id,
                    name = id.ToUpperInvariant(),
                    host = "10.0.0.1",
                    canExec = CanExec
                })
            });
            return Task.FromResult<object?>(JsonDocument.Parse(json).RootElement.Clone());
        }
    }

    private sealed class FakeFleetExec(bool progress = false) : IAgentTool
    {
        public string Name => "ssh_execute_fleet";
        public AITool Tool => null!;
        public bool Destructive => true;
        public bool ReadOnly => false;
        public string? ToolGroup => "command";

        public Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct) =>
            InvokeAsync(arguments, null, ct);

        public Task<object?> InvokeAsync(IDictionary<string, object?>? arguments,
            IProgress<ProgressNotificationValue>? reporter, CancellationToken ct)
        {
            var targets = (arguments?["serverIds"]?.ToString() ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (progress && reporter is not null)
            {
                var completed = 0;
                foreach (var target in targets)
                {
                    var message = JsonSerializer.Serialize(new
                    {
                        serverId = target,
                        serverName = target.ToUpperInvariant(),
                        host = "10.0.0.1",
                        success = true,
                        error = (string?)null,
                        output = "ok"
                    });
                    reporter.Report(new ProgressNotificationValue
                    {
                        Progress = ++completed,
                        Total = targets.Length,
                        Message = message
                    });
                }
            }
            var items = targets.Select(id => $"{{\"serverId\":\"{id}\",\"success\":true}}");
            var json = $$"""{"success":true,"summary":"ok","items":[{{string.Join(",", items)}}]}""";
            return Task.FromResult<object?>(JsonDocument.Parse(json).RootElement.Clone());
        }
    }
}
