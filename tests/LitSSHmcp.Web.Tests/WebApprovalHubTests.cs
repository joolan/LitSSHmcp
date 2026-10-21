using LitSSHmcp.Core.Services.Approval;
using LitSSHmcp.Web;
using LitSSHmcp.Web.Approvals;
using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>
/// 审批桥单元测试（报告 §3.3.1/§10.B.5）：owner 过滤推送、快照隔离、决策写回、孤儿清理。
/// hub 用临时目录 + 关闭后台轮询（pollInterval=0），测试直调 PollOnce。
/// </summary>
public class WebApprovalHubTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "litssh-webapprovals-" + Guid.NewGuid().ToString("N"));
    private readonly WebAccessLog _log = new();

    private WebApprovalHub NewHub() => new(_dir, _log, TimeSpan.Zero);

    private static string SeedPending(string dir, string id, string? owner)
    {
        ApprovalFileStore.WriteRequest(dir, new ApprovalRequestFile
        {
            Id = id,
            Server = "server-1",
            Operation = "exec",
            Command = "rm -rf /tmp/x",
            CreatedAt = DateTimeOffset.Now,
            ExpiresAt = DateTimeOffset.Now.AddMinutes(1),
            TimeoutSeconds = 60,
            Owner = owner
        });
        return id;
    }

    private static async Task<object> ReadFrameAsync(ApprovalPushClient client, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        return await client.Frames.ReadAsync(cts.Token);
    }

    [Fact]
    public async Task Register_sends_snapshot_with_only_own_items()
    {
        SeedPending(_dir, "aaa1", "101");
        SeedPending(_dir, "bbb2", "202");
        SeedPending(_dir, "ccc3", "local");
        using var hub = NewHub();

        var client = hub.Register("101");

        var frame = Assert.IsType<ApprovalSnapshotFrame>(await ReadFrameAsync(client));
        Assert.Equal("approval_snapshot", frame.Type);
        var item = Assert.Single(frame.Items);
        Assert.Equal("aaa1", item.Id);
        Assert.Equal("server-1", item.Server);
    }

    [Fact]
    public void Push_queue_is_bounded_and_drops_oldest_for_slow_clients()
    {
        using var hub = NewHub();
        var client = hub.Register("101");

        var total = ApprovalPushClient.FrameQueueCapacity + 32;
        for (var i = 0; i < total; i++)
            client.Enqueue(new ApprovalClosedFrame("approval_closed", i.ToString()));

        var frames = new List<object>();
        while (client.Frames.TryRead(out var frame))
            frames.Add(frame);

        Assert.Equal(ApprovalPushClient.FrameQueueCapacity, frames.Count);
        Assert.Equal((total - 1).ToString(),
            Assert.IsType<ApprovalClosedFrame>(frames[^1]).Id);
    }

    [Fact]
    public async Task PollOnce_pushes_new_pending_to_matching_owner_only()
    {
        using var hub = NewHub();
        var alice = hub.Register("101");
        var bob = hub.Register("202");
        // 注册时快照为空
        Assert.Empty(Assert.IsType<ApprovalSnapshotFrame>(await ReadFrameAsync(alice)).Items);
        Assert.Empty(Assert.IsType<ApprovalSnapshotFrame>(await ReadFrameAsync(bob)).Items);

        SeedPending(_dir, "ddd4", "101");
        SeedPending(_dir, "eee5", "202");
        hub.PollOnce();

        var pushed = Assert.IsType<ApprovalRequestFrame>(await ReadFrameAsync(alice));
        Assert.Equal("approval_request", pushed.Type);
        Assert.Equal("ddd4", pushed.Item.Id);

        var forBob = Assert.IsType<ApprovalRequestFrame>(await ReadFrameAsync(bob));
        Assert.Equal("eee5", forBob.Item.Id);

        // 已推过的不重复推
        hub.PollOnce();
        Assert.False(alice.Frames.TryRead(out _));
    }

    [Fact]
    public async Task PollOnce_never_pushes_local_or_missing_owner()
    {
        using var hub = NewHub();
        var client = hub.Register("101");

        SeedPending(_dir, "f001", "local");
        SeedPending(_dir, "f002", null);
        SeedPending(_dir, "f003", "");
        hub.PollOnce();

        // 快照（空）之后不应有任何推送
        Assert.Empty(Assert.IsType<ApprovalSnapshotFrame>(await ReadFrameAsync(client)).Items);
        Assert.False(client.Frames.TryRead(out _));
    }

    [Fact]
    public async Task PollOnce_closes_item_for_clients_that_knew_it()
    {
        SeedPending(_dir, "aaa1", "101");
        using var hub = NewHub();
        var alice = hub.Register("101");
        var bob = hub.Register("202");
        Assert.Single(Assert.IsType<ApprovalSnapshotFrame>(await ReadFrameAsync(alice)).Items);
        Assert.Empty(Assert.IsType<ApprovalSnapshotFrame>(await ReadFrameAsync(bob)).Items);

        File.Delete(ApprovalFileStore.PendingPath(_dir, "aaa1"));
        hub.PollOnce();

        // alice 快照里有 → 收到 closed；bob 从没见过 → 不推（per-client Known diff）
        var closedAlice = Assert.IsType<ApprovalClosedFrame>(await ReadFrameAsync(alice));
        Assert.Equal("approval_closed", closedAlice.Type);
        Assert.Equal("aaa1", closedAlice.Id);
        Assert.False(bob.Frames.TryRead(out _));
    }

    [Fact]
    public async Task PollOnce_closes_item_pushed_after_registration()
    {
        using var hub = NewHub();
        var alice = hub.Register("101");
        Assert.Empty(Assert.IsType<ApprovalSnapshotFrame>(await ReadFrameAsync(alice)).Items);

        SeedPending(_dir, "ddd4", "101");
        hub.PollOnce();
        var pushed = Assert.IsType<ApprovalRequestFrame>(await ReadFrameAsync(alice));
        Assert.Equal("ddd4", pushed.Item.Id);

        File.Delete(ApprovalFileStore.PendingPath(_dir, "ddd4"));
        hub.PollOnce();
        var closed = Assert.IsType<ApprovalClosedFrame>(await ReadFrameAsync(alice));
        Assert.Equal("ddd4", closed.Id);
    }

    [Fact]
    public async Task Decide_by_owner_writes_web_decision_and_notifies()
    {
        SeedPending(_dir, "aaa1", "101");
        using var hub = NewHub();
        var alice = hub.Register("101");
        Assert.Single(Assert.IsType<ApprovalSnapshotFrame>(await ReadFrameAsync(alice)).Items);

        var result = hub.Decide("101", "aaa1", approved: true, "alice", "127.0.0.1");

        Assert.Equal(ApprovalDecideResult.Approved, result);
        var decision = ApprovalFileStore.TryReadDecision(_dir, "aaa1");
        Assert.NotNull(decision);
        Assert.True(decision!.Approved);
        Assert.Equal("web", decision.Channel);

        var closed = Assert.IsType<ApprovalClosedFrame>(await ReadFrameAsync(alice));
        Assert.Equal("aaa1", closed.Id);
    }

    [Fact]
    public void Decide_by_other_owner_or_unknown_returns_not_found_without_decision()
    {
        SeedPending(_dir, "aaa1", "101");
        using var hub = NewHub();

        // 他人（或 "local" 这类非 Web 身份）一律 404，不写决策文件
        Assert.Equal(ApprovalDecideResult.NotFound, hub.Decide("202", "aaa1", true, "bob", null));
        Assert.Equal(ApprovalDecideResult.NotFound, hub.Decide("local", "aaa1", true, "bob", null));
        Assert.Equal(ApprovalDecideResult.NotFound, hub.Decide("101", "missing", true, "alice", null));
        Assert.Null(ApprovalFileStore.TryReadDecision(_dir, "aaa1"));
    }

    [Fact]
    public async Task Decide_rejection_writes_rejected_decision()
    {
        SeedPending(_dir, "aaa1", "101");
        using var hub = NewHub();

        var result = hub.Decide("101", "aaa1", approved: false, "alice", null);

        Assert.Equal(ApprovalDecideResult.Rejected, result);
        var decision = ApprovalFileStore.TryReadDecision(_dir, "aaa1");
        Assert.False(decision!.Approved);
        Assert.Equal("web", decision.Channel);
        await Task.CompletedTask;
    }

    [Fact]
    public void PollOnce_cleans_orphan_decisions_after_grace_but_keeps_fresh_ones()
    {
        using var hub = NewHub();

        // 老孤儿：无对应 pending 且超 10s 宽限 → 删除
        var stale = ApprovalFileStore.DecisionPath(_dir, "stale1");
        ApprovalFileStore.WriteDecision(_dir, new ApprovalDecisionFile { Id = "stale1", Approved = true, DecidedAt = DateTimeOffset.Now, Channel = "web" });
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddSeconds(-30));

        // 新孤儿：宽限期内保留（可能是 MCP 正常清理的毫秒窗口）
        ApprovalFileStore.WriteDecision(_dir, new ApprovalDecisionFile { Id = "fresh2", Approved = false, DecidedAt = DateTimeOffset.Now, Channel = "web" });

        hub.PollOnce();

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(ApprovalFileStore.DecisionPath(_dir, "fresh2")));
    }

    [Fact]
    public void PollOnce_keeps_decision_belonging_to_live_pending()
    {
        SeedPending(_dir, "aaa1", "101");
        ApprovalFileStore.WriteDecision(_dir, new ApprovalDecisionFile { Id = "aaa1", Approved = true, DecidedAt = DateTimeOffset.Now, Channel = "web" });
        var path = ApprovalFileStore.DecisionPath(_dir, "aaa1");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-30));

        using var hub = NewHub();
        hub.PollOnce();

        Assert.True(File.Exists(path));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }
}
