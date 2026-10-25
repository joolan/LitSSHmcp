using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class WorkLogStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litssh-worklog-" + Guid.NewGuid().ToString("N"));
    private readonly string _workspace;

    public WorkLogStoreTests()
    {
        _workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(_workspace);
    }

    private WorkLogStore CreateStore(Action<AgentWorkLogConfig>? configure = null)
    {
        var config = new AgentWorkLogConfig { Enabled = true, Dir = Path.Combine(_root, "logs") };
        configure?.Invoke(config);
        return WorkLogStore.TryCreate(config, _workspace, null, scopeByOwner: false)!;
    }

    private static WorkLogTurn Turn(bool succeeded = true, params WorkLogToolCall[] calls)
    {
        var turn = new WorkLogTurn(null, "重启 nginx 并验证", DateTime.Now)
        {
            Succeeded = succeeded,
            AssistantText = "已完成"
        };
        turn.Calls.AddRange(calls);
        return turn;
    }

    private static WorkLogToolCall Call(string name, bool ok = true, bool readOnly = false,
        bool destructive = true, string? result = "ok")
        => new(name,
            new Dictionary<string, object?>
            {
                ["serverId"] = "web01",
                ["command"] = "systemctl restart nginx"
            },
            ok, readOnly, destructive, 1200, result);

    [Fact]
    public async Task Successful_mutating_turn_is_appended_to_local_date_file()
    {
        var store = CreateStore();

        var path = await store.AppendTurnAsync(Turn(calls: Call("ssh_execute_command")));

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(store.GetDailyPath(DateTime.Now), path);
        var text = await File.ReadAllTextAsync(path!);
        Assert.Contains("# " + DateTime.Now.ToString("yyyy-MM-dd"), text);
        Assert.Contains("请求: 重启 nginx 并验证", text);
        Assert.Contains("ssh_execute_command", text);
        Assert.Contains("ssh:web01", text);
        Assert.Contains("systemctl restart nginx", text);
        Assert.Contains("成功", text);
    }

    [Fact]
    public async Task Read_only_turn_is_skipped_by_default_and_can_be_enabled()
    {
        var skipped = CreateStore();
        Assert.Null(await skipped.AppendTurnAsync(
            Turn(calls: Call("ssh_snapshot_get", readOnly: true, destructive: false))));

        var enabled = CreateStore(c => c.IncludeReadOnly = true);
        var path = await enabled.AppendTurnAsync(
            Turn(calls: Call("ssh_snapshot_get", readOnly: true, destructive: false)));

        Assert.NotNull(path);
        Assert.Contains("调查", await File.ReadAllTextAsync(path!));
    }

    [Fact]
    public async Task Failed_turn_is_skipped_unless_include_failures()
    {
        var skipped = CreateStore(c => c.IncludeFailures = false);
        Assert.Null(await skipped.AppendTurnAsync(
            Turn(succeeded: false, calls: Call("ssh_execute_command", ok: false, result: "rejected"))));

        var enabled = CreateStore(c => c.IncludeFailures = true);
        var path = await enabled.AppendTurnAsync(
            Turn(succeeded: false, calls: Call("ssh_execute_command", ok: false, result: "rejected")));

        Assert.NotNull(path);
        var text = await File.ReadAllTextAsync(path!);
        Assert.Contains("失败", text);
        Assert.Contains("rejected", text);
    }

    [Fact]
    public async Task Secrets_are_redacted_before_write()
    {
        var store = CreateStore();
        var call = new WorkLogToolCall("ssh_execute_command",
            new Dictionary<string, object?>
            {
                ["serverId"] = "web01",
                ["command"] = "mysql --password=FAKE_PASSWORD_FOR_TEST -e 'select 1'"
            },
            true, false, true, 5, "ok");

        var path = await store.AppendTurnAsync(Turn(calls: call));
        var text = await File.ReadAllTextAsync(path!);

        Assert.DoesNotContain("FAKE_PASSWORD_FOR_TEST", text);
        Assert.Contains("******", text);
    }

    [Fact]
    public async Task Model_note_appends_to_same_daily_file()
    {
        var store = CreateStore();

        var result = await store.AppendNoteAsync("目标：重启 nginx\n结果：健康检查通过");

        Assert.Contains("已追加", result);
        var text = await File.ReadAllTextAsync(store.GetDailyPath(DateTime.Now));
        Assert.Contains("复盘", text);
        Assert.Contains("健康检查通过", text);
    }

    [Fact]
    public async Task Worklog_tool_appends_model_note()
    {
        var store = CreateStore();
        var tool = Assert.Single(WorkLogTools.Create(store));

        Assert.Equal("ops_worklog_write", tool.Name);
        Assert.False(tool.ReadOnly);
        var result = await tool.InvokeAsync(
            new Dictionary<string, object?> { ["content"] = "目标：重启\n结果：通过" }, default);

        Assert.Contains("已追加", result!.ToString());
        var text = await File.ReadAllTextAsync(store.GetDailyPath(DateTime.Now));
        Assert.Contains("复盘", text);
        Assert.Contains("结果：通过", text);
    }

    [Fact]
    public async Task Compaction_marker_only_written_when_mutating_tools_present()
    {
        var store = CreateStore();

        await store.AppendCompactionAsync("agent-1", 2, 10, new[] { "重启 nginx" }, Array.Empty<string>());
        Assert.False(File.Exists(store.GetDailyPath(DateTime.Now)));

        await store.AppendCompactionAsync("agent-1", 2, 10, new[] { "重启 nginx" },
            new[] { "ssh_execute_command" });
        Assert.True(File.Exists(store.GetDailyPath(DateTime.Now)));
    }

    [Fact]
    public async Task Ops_doc_write_cannot_tamper_worklogs()
    {
        var tools = WorkspaceTools.Create(_workspace);
        var write = tools.Single(t => t.Name == "ops_doc_write");

        var result = await write.InvokeAsync(new Dictionary<string, object?>
        {
            ["path"] = "worklogs/2026/2026-01-01.md",
            ["content"] = "tampered"
        }, default);

        Assert.Contains("宿主维护", result!.ToString());
        Assert.False(File.Exists(Path.Combine(_workspace, "worklogs", "2026", "2026-01-01.md")));
    }

    [Fact]
    public void Disabled_config_returns_null()
    {
        var config = new AgentWorkLogConfig { Enabled = false };
        Assert.Null(WorkLogStore.TryCreate(config, _workspace, null, scopeByOwner: false));
    }

    [Fact]
    public void Default_dir_is_workspace_worklogs()
    {
        var store = WorkLogStore.TryCreate(new AgentWorkLogConfig { Enabled = true }, _workspace, null, false)!;
        Assert.Equal(Path.Combine(Path.GetFullPath(_workspace), WorkLogStore.DirName), store.RootDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
