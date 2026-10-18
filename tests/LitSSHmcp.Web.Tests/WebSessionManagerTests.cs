using LitSSHmcp.Agent;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Web;
using LitSSHmcp.Web.Sessions;
using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>Web 会话宿主单元测试：owner 隔离/固定根目录/轮次闸门/停止/删除清理（报告 D4/§10.B.5）。</summary>
public class WebSessionManagerTests
{
    private readonly FakeRuntimeFactory _factory = new();
    private readonly ContextStore _store;
    private readonly WebSessionManager _manager;
    private readonly WebPrincipal _alice = new(101, "alice", "user");
    private readonly WebPrincipal _bob = new(202, "bob", "user");

    public WebSessionManagerTests()
    {
        var dbPath = Path.Combine(WebTestEnv.DataDir, $"agent-{Guid.NewGuid():N}.db");
        _store = new ContextStore(dbPath);
        _store.InitializeAsync().GetAwaiter().GetResult();
        _manager = new WebSessionManager(_store, _factory, new WebAccessLog());
    }

    [Fact]
    public async Task Create_session_assigns_owner_and_fixed_root_dir()
    {
        var info = await _manager.CreateSessionAsync(_alice, "测试任务");

        Assert.Equal("101", info.OwnerUserId);
        Assert.Equal("测试任务", info.Title);
        Assert.StartsWith(Path.Combine(ConfigPaths.AppDataDir, "jobs"), info.WorkspaceDir, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("101", "s" + info.Id), info.WorkspaceDir, StringComparison.Ordinal);
        Assert.True(Directory.Exists(info.WorkspaceDir));
        Assert.Equal("101", await _store.GetSessionOwnerUserIdAsync(info.Id));
        Assert.Equal(info.WorkspaceDir, await _store.GetSessionWorkspaceDirAsync(info.Id));
    }

    [Fact]
    public async Task List_sessions_filters_by_owner_and_hides_local()
    {
        var mine = await _manager.CreateSessionAsync(_alice, "我的");
        var other = await _manager.CreateSessionAsync(_bob, "别人的");
        var localId = await _store.CreateSessionAsync("桌面会话");
        await _store.SetSessionOwnerUserIdAsync(localId, "local");

        var list = await _manager.ListSessionsAsync(_alice);

        Assert.Contains(list, s => s.Id == mine.Id);
        Assert.DoesNotContain(list, s => s.Id == other.Id);
        Assert.DoesNotContain(list, s => s.Id == localId);
    }

    [Fact]
    public async Task Foreign_owner_get_messages_throws_not_found()
    {
        var session = await _manager.CreateSessionAsync(_alice, "私有");

        await Assert.ThrowsAsync<WebSessionNotFoundException>(
            () => _manager.GetMessagesAsync(_bob, session.Id));
        await Assert.ThrowsAsync<WebSessionNotFoundException>(
            () => _manager.RunTurnAsync(_bob, session.Id, "hi", new Recorder()));
        await Assert.ThrowsAsync<WebSessionNotFoundException>(
            () => _manager.StopTurnAsync(_bob, session.Id));
        await Assert.ThrowsAsync<WebSessionNotFoundException>(
            () => _manager.DeleteSessionAsync(_bob, session.Id));
    }

    [Fact]
    public async Task Run_turn_persists_messages_and_forwards_events_in_order()
    {
        var session = await _manager.CreateSessionAsync(_alice, "轮次");
        _factory.OnSend = (text, progress, _) =>
        {
            progress!.Report(new AgentEvent(AgentEventKind.AssistantText, Text: "he"));
            progress.Report(new AgentEvent(AgentEventKind.ToolCall, ToolName: "bash", ArgumentsJson: "{}"));
            progress.Report(new AgentEvent(AgentEventKind.AssistantText, Text: "llo"));
            return Task.CompletedTask;
        };
        var recorder = new Recorder();

        await _manager.RunTurnAsync(_alice, session.Id, "你好", recorder);

        Assert.Equal(
            new[] { AgentEventKind.AssistantText, AgentEventKind.ToolCall, AgentEventKind.AssistantText },
            recorder.Events.Select(e => e.Kind).ToArray());

        var messages = await _store.GetMessagesAsync(session.Id);
        Assert.Equal(2, messages.Count);
        Assert.Equal(("user", "你好"), (messages[0].Role, messages[0].Content));
        Assert.Equal(("assistant", "hello"), (messages[1].Role, messages[1].Content));
        Assert.Equal("summary", await _store.GetSessionSummaryAsync(session.Id));
    }

    [Fact]
    public async Task Runtime_created_once_with_owner_dir_and_history()
    {
        var session = await _manager.CreateSessionAsync(_alice, "历史");
        // 模拟上一次运行已落库的对话（运行时首次装配时按库内消息重建历史）
        await _store.AppendMessageAsync(session.Id, "user", "旧问");
        await _store.AppendMessageAsync(session.Id, "assistant", "旧答");
        await _store.AppendMessageAsync(session.Id, "tool", "工具轨迹");
        _factory.OnSend = (_, progress, _) =>
        {
            progress!.Report(new AgentEvent(AgentEventKind.AssistantText, Text: "答"));
            return Task.CompletedTask;
        };

        await _manager.RunTurnAsync(_alice, session.Id, "第一问", new Recorder());
        await _manager.RunTurnAsync(_alice, session.Id, "第二问", new Recorder());

        var request = Assert.Single(_factory.Requests);
        Assert.Equal("101", request.OwnerUserId);
        Assert.Equal(session.WorkspaceDir, request.WorkspaceDir);
        Assert.Equal(new[] { "user", "assistant" }, request.History.Select(m => m.Role.Value).ToArray());
        Assert.Equal(new[] { "旧问", "旧答" }, request.History.Select(m => m.Text).ToArray());
    }

    [Fact]
    public async Task Run_turn_second_concurrent_throws_busy()
    {
        var session = await _manager.CreateSessionAsync(_alice, "忙");
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        _factory.OnSend = async (_, progress, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            progress!.Report(new AgentEvent(AgentEventKind.AssistantText, Text: "完"));
        };

        var turn1 = _manager.RunTurnAsync(_alice, session.Id, "a", new Recorder());
        await entered.Task;
        await Assert.ThrowsAsync<WebSessionBusyException>(
            () => _manager.RunTurnAsync(_alice, session.Id, "b", new Recorder()));

        release.SetResult();
        await turn1;
        // 闸门释放后可继续下一轮
        await _manager.RunTurnAsync(_alice, session.Id, "c", new Recorder());
    }

    [Fact]
    public async Task Stop_turn_cancels_running_turn()
    {
        var session = await _manager.CreateSessionAsync(_alice, "停");
        var entered = new TaskCompletionSource();
        _factory.OnSend = async (_, _, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };

        var turn = _manager.RunTurnAsync(_alice, session.Id, "跑", new Recorder());
        await entered.Task;
        await _manager.StopTurnAsync(_alice, session.Id);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);
        // 停止只取消本轮，会话仍可用
        Assert.Contains(session.Id, (await _manager.ListSessionsAsync(_alice)).Select(s => s.Id));
    }

    [Fact]
    public async Task Delete_session_disposes_runtime_removes_dir_and_row()
    {
        var session = await _manager.CreateSessionAsync(_alice, "删");
        _factory.OnSend = (_, progress, _) =>
        {
            progress!.Report(new AgentEvent(AgentEventKind.AssistantText, Text: "x"));
            return Task.CompletedTask;
        };
        await _manager.RunTurnAsync(_alice, session.Id, "跑", new Recorder());
        var dir = session.WorkspaceDir;
        File.WriteAllText(Path.Combine(dir, "out.txt"), "产出");

        await _manager.DeleteSessionAsync(_alice, session.Id);

        Assert.False(Directory.Exists(dir));
        Assert.Empty(await _manager.ListSessionsAsync(_alice));
        Assert.True(_factory.Disposed);
    }

    [Fact]
    public void Build_history_keeps_only_text_roles()
    {
        var rows = new List<AgentMessageRow>
        {
            new("user", "q1", "t"),
            new("assistant", "a1", "t"),
            new("tool", "输出", "t"),
            new("reasoning", "思考", "t"),
            new("assistant", "", "t")
        };

        var history = WebSessionManager.BuildHistory(rows);

        Assert.Equal(new[] { "q1", "a1" }, history.Select(m => m.Text).ToArray());
        Assert.Equal(new[] { "user", "assistant" }, history.Select(m => m.Role.Value).ToArray());
    }

    private sealed class Recorder : IProgress<AgentEvent>
    {
        public List<AgentEvent> Events { get; } = new();
        public void Report(AgentEvent value) { lock (Events) Events.Add(value); }
    }
}

/// <summary>可编排的假运行时：记录装配请求，按 OnSend 编排事件流。</summary>
public sealed class FakeRuntimeFactory : IWebRuntimeFactory
{
    public List<WebRuntimeRequest> Requests { get; } = new();
    public Func<string, IProgress<AgentEvent>?, CancellationToken, Task>? OnSend { get; set; }
    public bool Disposed { get; private set; }
    public string Summary { get; set; } = "summary";

    public Task<IWebRuntime> CreateAsync(WebRuntimeRequest request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request);
        return Task.FromResult<IWebRuntime>(new FakeRuntime(this));
    }

    private sealed class FakeRuntime(FakeRuntimeFactory owner) : IWebRuntime
    {
        public string Summary => owner.Summary;

        public Task SendAsync(string userText, IProgress<AgentEvent>? progress, CancellationToken ct)
        {
            if (owner.OnSend is not null)
                return owner.OnSend(userText, progress, ct);
            progress?.Report(new AgentEvent(AgentEventKind.AssistantText, Text: "echo:" + userText));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            owner.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
