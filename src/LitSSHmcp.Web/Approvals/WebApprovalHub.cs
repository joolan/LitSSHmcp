using System.Collections.Concurrent;
using System.Threading.Channels;
using LitSSHmcp.Core.Services.Approval;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Web.Approvals;

/// <summary>审批条目（REST 列表与 WS 帧共用）。</summary>
public sealed record ApprovalItemDto(
    string Id, string Server, string Operation, string Command, string? FilePath,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, int TimeoutSeconds);

public sealed record ApprovalSnapshotFrame(string Type, ApprovalItemDto[] Items);
public sealed record ApprovalRequestFrame(string Type, ApprovalItemDto Item);
public sealed record ApprovalClosedFrame(string Type, string Id);

public enum ApprovalDecideResult
{
    Approved,
    Rejected,
    /// <summary>请求不存在或 Owner 不是本人（统一 404，不泄露存在性）。</summary>
    NotFound
}

/// <summary>
/// 推送客户端句柄：端点注册后从 <see cref="Frames"/> 消费，Hub 经内部队列保序推送。
/// <see cref="Known"/> = 已推给该客户端的 pending id 集合（Hub 的 _gate 下读写），
/// 每轮据此做 per-client 增量 diff，保证新增/消失对每个连接都不漏。
/// </summary>
public sealed class ApprovalPushClient
{
    private readonly Channel<object> _frames = Channel.CreateUnbounded<object>();

    internal ApprovalPushClient(string userId) => UserId = userId;

    public Guid Id { get; } = Guid.NewGuid();
    public string UserId { get; }
    public ChannelReader<object> Frames => _frames.Reader;
    internal HashSet<string> Known { get; } = new(StringComparer.Ordinal);
    internal void Enqueue(object frame) => _frames.Writer.TryWrite(frame);
    internal void Complete() => _frames.Writer.TryComplete();
}

/// <summary>
/// Web 审批桥（报告 §3.3.1「沿用 cli 文件通道加 Web 推送」）：轮询 MCP 子进程写的
/// pending-&lt;id&gt;.json，把 <b>本用户</b> 的新请求经 /ws/approvals 推给浏览器；决策经 REST 写回
/// decision-&lt;id&gt;.json（Channel="web"），MCP 子进程 ≤500ms 消化。不动 MCP 传输、无需 web 通道。
/// 身份过滤：pending.Owner（子进程从 LITSSH_OWNER_USER_ID 注入的归因）须等于登录用户 Id 才推送/可决策
/// （§10.B.5 会话隔离的审批延伸；"local"/null 永不推 Web，仅桌面/CLI 可见）。
/// 注册/轮询在 <c>_gate</c> 下互斥；每个客户端按自身 Known 集合做增量 diff（快照即 Known 初值），
/// 快照与后续推送对连接原子（客户端按 id 幂等 upsert）。
/// </summary>
public sealed class WebApprovalHub : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan OrphanGrace = TimeSpan.FromSeconds(10);

    private readonly string _directory;
    private readonly WebAccessLog _accessLog;
    private readonly ConcurrentDictionary<Guid, ApprovalPushClient> _clients = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _loop;

    /// <summary>生产构造：默认 approvals 目录 + 500ms 轮询。</summary>
    public WebApprovalHub(WebAccessLog accessLog)
        : this(ConfigPaths.ApprovalsDir, accessLog, PollInterval)
    {
    }

    /// <summary>测试构造：自定目录；<paramref name="pollInterval"/>&lt;=0 不启后台循环（测试直调 <see cref="PollOnce"/>）。</summary>
    internal WebApprovalHub(string directory, WebAccessLog accessLog, TimeSpan pollInterval)
    {
        _directory = directory;
        _accessLog = accessLog;
        if (pollInterval > TimeSpan.Zero)
            _loop = Task.Run(() => PollLoopAsync(pollInterval, _cts.Token));
    }

    /// <summary>注册连接：快照与注册在闸门内原子完成，快照即该客户端 Known 初值，此后增量不越过快照。</summary>
    public ApprovalPushClient Register(string userId)
    {
        lock (_gate)
        {
            var client = new ApprovalPushClient(userId);
            _clients[client.Id] = client;
            var items = SnapshotForCore(userId);
            foreach (var item in items)
                client.Known.Add(item.Id);
            client.Enqueue(new ApprovalSnapshotFrame("approval_snapshot", items));
            return client;
        }
    }

    public void Unregister(ApprovalPushClient client)
    {
        if (_clients.TryRemove(client.Id, out var removed))
            removed.Complete();
    }

    /// <summary>当前待决列表（仅本用户；REST 快照/手动刷新用）。</summary>
    public IReadOnlyList<ApprovalItemDto> SnapshotFor(string userId)
        => SnapshotForCore(userId);

    /// <summary>
    /// 写回决策（Channel="web"）。Owner 校验在内存匹配阶段完成，写入用文件内 Id（服务端生成），
    /// 外部输入不进路径（防注入）。
    /// </summary>
    public ApprovalDecideResult Decide(string userId, string id, bool approved, string username, string? ip)
    {
        var pending = ApprovalFileStore.ListPending(_directory)
            .FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        if (pending is null || !OwnerMatches(pending.Owner, userId))
        {
            _accessLog.Write("approval_denied", username, ip, $"id={id} not_found_or_not_owner");
            return ApprovalDecideResult.NotFound;
        }

        ApprovalFileStore.WriteDecision(_directory, new ApprovalDecisionFile
        {
            Id = pending.Id,
            Approved = approved,
            DecidedAt = DateTimeOffset.Now,
            Channel = "web"
        });
        _accessLog.Write(approved ? "approval_allowed" : "approval_denied", username, ip,
            $"id={pending.Id} server={pending.Server} op={pending.Operation}");

        // 即时通知本人（不动 Known：pending 文件还在，动了会被下一轮当"新增"重推；
        // MCP 清理文件后的下一轮 diff 会再补一条 closed，客户端按 id 幂等）
        var closed = new ApprovalClosedFrame("approval_closed", pending.Id);
        foreach (var client in _clients.Values)
        {
            if (client.UserId == userId)
                client.Enqueue(closed);
        }

        return approved ? ApprovalDecideResult.Approved : ApprovalDecideResult.Rejected;
    }

    /// <summary>扫描一次 pending 目录：按客户端 Known 做增量 diff（新增推送/消失关闭）、清理孤儿决策。测试直调。</summary>
    internal void PollOnce()
    {
        lock (_gate)
        {
            var pending = ApprovalFileStore.ListPending(_directory);
            RemoveOrphanDecisions(pending);

            foreach (var client in _clients.Values)
            {
                var mine = pending.Where(p => OwnerMatches(p.Owner, client.UserId)).ToArray();
                foreach (var request in mine)
                {
                    if (client.Known.Add(request.Id))
                    {
                        client.Enqueue(new ApprovalRequestFrame("approval_request", ToDto(request)));
                        _accessLog.Write("approval_pushed", client.UserId, null,
                            $"id={request.Id} server={request.Server}");
                    }
                }

                if (client.Known.Count == 0)
                    continue;
                var mineIds = mine.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var id in client.Known.ToArray())
                {
                    if (!mineIds.Contains(id))
                    {
                        client.Known.Remove(id);
                        client.Enqueue(new ApprovalClosedFrame("approval_closed", id));
                    }
                }
            }
        }
    }

    private ApprovalItemDto[] SnapshotForCore(string userId)
    {
        return ApprovalFileStore.ListPending(_directory)
            .Where(p => OwnerMatches(p.Owner, userId))
            .Select(ToDto)
            .ToArray();
    }

    /// <summary>Owner 须非空且非 "local"（桌面未登录不算可推送身份），且等于登录用户 Id。</summary>
    private static bool OwnerMatches(string? owner, string userId)
        => !string.IsNullOrWhiteSpace(owner)
           && !string.Equals(owner, "local", StringComparison.Ordinal)
           && string.Equals(owner, userId, StringComparison.Ordinal);

    private static ApprovalItemDto ToDto(ApprovalRequestFile request)
        => new(request.Id, request.Server, request.Operation, request.Command, request.FilePath,
            request.CreatedAt, request.ExpiresAt, request.TimeoutSeconds);

    /// <summary>
    /// 清理无对应 pending 的 decision 文件（MCP finally 双删后 Web 才写入的迟到决策）。
    /// 10s 宽限避开「pending 先删、decision 后删」的正常清理窗口。
    /// </summary>
    private void RemoveOrphanDecisions(IReadOnlyList<ApprovalRequestFile> pending)
    {
        try
        {
            if (!Directory.Exists(_directory))
                return;
            var pendingIds = pending.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(_directory, "decision-*.json"))
            {
                var name = Path.GetFileName(file);
                if (name.Length <= "decision-".Length + ".json".Length)
                    continue;
                var id = name["decision-".Length .. ^".json".Length];
                if (pendingIds.Contains(id))
                    continue;
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < OrphanGrace)
                        continue;
                    File.Delete(file);
                }
                catch
                {
                    // 单文件删除失败忽略
                }
            }
        }
        catch
        {
            // 目录枚举失败忽略（下次轮询重试）
        }
    }

    private async Task PollLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    PollOnce();
                }
                catch
                {
                    // 轮询单次失败不退出循环
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主关闭
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* ignore */ }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        foreach (var client in _clients.Values)
            client.Complete();
        _cts.Dispose();
    }
}
