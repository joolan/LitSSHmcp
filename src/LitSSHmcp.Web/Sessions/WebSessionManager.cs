using System.Collections.Concurrent;
using System.Text;
using LitSSHmcp.Agent;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Web;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Web.Sessions;

/// <summary>Web 会话摘要（REST/WS 共用）。</summary>
public sealed record WebSessionInfo(long Id, string Title, string OwnerUserId, string WorkspaceDir, string UpdatedAt);

/// <summary>不属于当前用户（或会话不存在）——对调用方统一表现为 404，不泄露存在性。</summary>
public sealed class WebSessionNotFoundException() : Exception("会话不存在");

/// <summary>同一会话上一轮仍在进行。</summary>
public sealed class WebSessionBusyException() : Exception("上一轮仍在进行中");

/// <summary>超出每用户并发会话上限（§10.B.6 配额限速）——端点对外映射 409。</summary>
public sealed class WebSessionLimitException(string message) : Exception(message);

/// <summary>路径越出会话目录（<c>..</c>、绝对路径、链接逃逸、根外目录）——对外统一 404，越权尝试落 file_denied。</summary>
public sealed class WebFilePathDeniedException() : Exception("路径越出会话目录");

/// <summary>会话目录条目（目录浏览；非递归，Path 相对会话根、'/' 分隔）。</summary>
public sealed record WebFileEntry(string Path, string Name, bool IsDir, long? Size, string ModifiedAt);

/// <summary>下载句柄：流由端点随 HTTP 响应释放。</summary>
public sealed record WebFileDownload(string FileName, FileStream Content, long Length);

/// <summary>
/// Web 会话宿主（评估报告 D4/§3.2）：每会话独立 AgentRuntime（stdio MCP 子进程），
/// 会话目录固定根 <c>%APPDATA%\LitSSH\jobs\&lt;user&gt;\s&lt;id&gt;</c>（§8 遗留 #3），
/// owner = 登录用户 Id（D1 归因贯穿），所有读写按 owner 校验（§10.B.5 会话隔离）。
/// </summary>
public sealed class WebSessionManager : IAsyncDisposable
{
    private readonly IContextStore _store;
    private readonly IWebRuntimeFactory _runtimeFactory;
    private readonly WebAccessLog _accessLog;
    private readonly IConfigService _configService;
    private readonly ConcurrentDictionary<long, SessionSlot> _slots = new();

    public WebSessionManager(IContextStore store, IWebRuntimeFactory runtimeFactory,
        WebAccessLog accessLog, IConfigService configService)
    {
        _store = store;
        _runtimeFactory = runtimeFactory;
        _accessLog = accessLog;
        _configService = configService;
    }

    /// <summary>Web 会话目录固定根默认值：&lt;data&gt;/jobs（评估报告 §8.3）。</summary>
    public static string DefaultWebJobsRoot => Path.Combine(ConfigPaths.AppDataDir, WorkspaceLayout.JobsDirName);

    private static string _webJobsRoot = DefaultWebJobsRoot;

    /// <summary>
    /// Web 会话目录固定根（评估报告 §8.3 管理员可配）：默认 <c>%APPDATA%\LitSSH\jobs</c>，
    /// 会话目录为其下 <c>&lt;用户Id&gt;/s&lt;会话Id&gt;</c>。由 <see cref="ConfigureJobsRoot"/> 在宿主启动时应用
    /// （改动需重启 Web），文件浏览/删除的"必须位于固定根之下"校验随之跟随新根。
    /// </summary>
    public static string WebJobsRoot => _webJobsRoot;

    /// <summary>
    /// 解析配置值 → 固定根（纯函数）：空白/相对路径/规范化失败一律回退默认根（管理员配置错误不阻断启动）。
    /// </summary>
    public static string ResolveJobsRoot(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return DefaultWebJobsRoot;
        var trimmed = configured.Trim();
        if (!Path.IsPathRooted(trimmed))
            return DefaultWebJobsRoot;
        return PathGuard.TryCanonicalize(trimmed) ?? DefaultWebJobsRoot;
    }

    /// <summary>宿主启动时应用配置（§8.3）；null/空恢复默认根。</summary>
    public static void ConfigureJobsRoot(string? configured) => _webJobsRoot = ResolveJobsRoot(configured);

    private sealed class SessionSlot
    {
        public IWebRuntime? Runtime;
        public string WorkspaceDir = string.Empty;
        public readonly SemaphoreSlim TurnGate = new(1, 1);
        public CancellationTokenSource? TurnCts;
    }

    /// <summary>IProgress 同步转发器：Report 直接回调，不排队到线程池，保持事件顺序。</summary>
    private sealed class SyncProgress(Action<AgentEvent> handler) : IProgress<AgentEvent>
    {
        public void Report(AgentEvent value) => handler(value);
    }

    /// <summary>创建会话：owner=登录用户，立即分配固定根下会话目录并落库锁定。</summary>
    public async Task<WebSessionInfo> CreateSessionAsync(WebPrincipal owner, string? title, CancellationToken ct = default)
    {
        // 每用户并发会话上限（评估报告 §10.B.6 配额限速；0=不限）
        var config = await _configService.LoadConfigAsync();
        var cap = config.Security?.Limits?.MaxSessionsPerUser ?? 0;
        if (cap > 0 && (await ListSessionsAsync(owner, ct)).Count >= cap)
        {
            _accessLog.Write("session_limit_reached", owner.Username, null, $"cap={cap}");
            throw new WebSessionLimitException($"并发会话数已达上限（{cap}），请先删除不再使用的会话");
        }
        var id = await _store.CreateSessionAsync(string.IsNullOrWhiteSpace(title) ? "Web 任务" : title!.Trim(), ct);
        var dir = WorkspaceLayout.ResolveSessionDirInJobsRoot(WebJobsRoot, owner.UserId.ToString(), id);
        Directory.CreateDirectory(dir);
        await _store.SetSessionOwnerUserIdAsync(id, owner.UserId.ToString(), ct);
        await _store.SetSessionWorkspaceDirAsync(id, dir, ct);
        _accessLog.Write("session_create", owner.Username, null, $"session={id}");
        var row = (await _store.ListSessionsAsync(ct)).First(s => s.Id == id);
        return new WebSessionInfo(id, row.Title, owner.UserId.ToString(), dir, row.UpdatedAt);
    }

    /// <summary>列出当前用户的会话（逐条按 owner 过滤；桌面 local 会话不出现在 Web 列表）。</summary>
    public async Task<IReadOnlyList<WebSessionInfo>> ListSessionsAsync(WebPrincipal owner, CancellationToken ct = default)
    {
        var rows = await _store.ListSessionsAsync(ct);
        var result = new List<WebSessionInfo>();
        foreach (var row in rows)
        {
            var sessionOwner = await _store.GetSessionOwnerUserIdAsync(row.Id, ct);
            if (!string.Equals(sessionOwner, owner.UserId.ToString(), StringComparison.Ordinal))
                continue;
            var dir = await _store.GetSessionWorkspaceDirAsync(row.Id, ct);
            result.Add(new WebSessionInfo(row.Id, row.Title, sessionOwner, dir, row.UpdatedAt));
        }
        return result;
    }

    /// <summary>读取消息历史（owner 校验；非本用户抛 <see cref="WebSessionNotFoundException"/>）。</summary>
    public async Task<IReadOnlyList<AgentMessageRow>> GetMessagesAsync(WebPrincipal owner, long sessionId, CancellationToken ct = default)
    {
        await EnsureOwnerAsync(owner, sessionId, ct);
        return await _store.GetMessagesAsync(sessionId, ct);
    }

    /// <summary>连接/握手时的 owner 预检（不泄露存在性；逐消息仍经各 API 复检）。</summary>
    public async Task<bool> IsOwnerAsync(WebPrincipal owner, long sessionId, CancellationToken ct = default)
    {
        var sessionOwner = await _store.GetSessionOwnerUserIdAsync(sessionId, ct);
        return string.Equals(sessionOwner, owner.UserId.ToString(), StringComparison.Ordinal);
    }

    /// <summary>删除会话：owner 校验 → 停轮次 → 释放运行时 → 清理会话目录（jobs/ 子树物理防护）→ 删库行。</summary>
    public async Task DeleteSessionAsync(WebPrincipal owner, long sessionId, CancellationToken ct = default)
    {
        await EnsureOwnerAsync(owner, sessionId, ct);
        if (_slots.TryRemove(sessionId, out var slot))
        {
            try { slot.TurnCts?.Cancel(); } catch { /* 停轮失败忽略 */ }
            if (slot.Runtime is not null)
            {
                try { await slot.Runtime.DisposeAsync(); } catch { /* 释放失败忽略 */ }
            }
            slot.TurnGate.Dispose();
        }
        var dir = await _store.GetSessionWorkspaceDirAsync(sessionId, ct);
        if (!string.IsNullOrWhiteSpace(dir))
            WorkspaceLayout.TryRemoveSessionDirInJobsRoot(WebJobsRoot, dir);
        await _store.DeleteSessionAsync(sessionId, ct);
        _accessLog.Write("session_delete", owner.Username, null, $"session={sessionId}");
    }

    /// <summary>
    /// 发送一轮消息并流式回吐事件（owner 校验 + 每会话一轮闸门 + 可取消）。
    /// 轮次成功后把用户/助手消息落库并触达会话；失败/取消不落助手消息。
    /// </summary>
    public async Task RunTurnAsync(WebPrincipal owner, long sessionId, string userText,
        IProgress<AgentEvent> progress, CancellationToken ct = default)
    {
        await EnsureOwnerAsync(owner, sessionId, ct);
        if (string.IsNullOrWhiteSpace(userText))
            throw new ArgumentException("消息不能为空", nameof(userText));

        var slot = _slots.GetOrAdd(sessionId, _ => new SessionSlot());
        if (!await slot.TurnGate.WaitAsync(0, ct))
        {
            _accessLog.Write("turn_rejected_busy", owner.Username, null, $"session={sessionId}");
            throw new WebSessionBusyException();
        }

        var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        slot.TurnCts = turnCts;
        try
        {
            await EnsureRuntimeAsync(owner, sessionId, slot, turnCts.Token);
            await _store.AppendMessageAsync(sessionId, "user", userText, turnCts.Token);

            var assistantText = new StringBuilder();
            // 同步转发（不经 Progress<T> 线程池派发），保持事件到达顺序
            var sink = new SyncProgress(e =>
            {
                if (e.Kind == AgentEventKind.AssistantText && e.Text is not null)
                    assistantText.Append(e.Text);
                progress.Report(e);
            });
            using var auditScope = LocalAudit.BeginRunScope(sessionId.ToString());
            await slot.Runtime!.SendAsync(userText, sink, turnCts.Token);

            if (assistantText.Length > 0)
                await _store.AppendMessageAsync(sessionId, "assistant", assistantText.ToString(), CancellationToken.None);
            try { await _store.SetSessionSummaryAsync(sessionId, slot.Runtime.Summary, CancellationToken.None); } catch { /* 摘要失败不阻断 */ }
            try { await _store.TouchSessionAsync(sessionId, CancellationToken.None); } catch { /* 触达失败忽略 */ }
            _accessLog.Write("turn_done", owner.Username, null, $"session={sessionId}");
        }
        catch (OperationCanceledException)
        {
            _accessLog.Write("turn_cancelled", owner.Username, null, $"session={sessionId}");
            throw;
        }
        catch (WebSessionBusyException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _accessLog.Write("turn_error", owner.Username, null, $"session={sessionId}: {ex.GetType().Name}");
            throw;
        }
        finally
        {
            if (ReferenceEquals(slot.TurnCts, turnCts))
                slot.TurnCts = null;
            turnCts.Dispose();
            slot.TurnGate.Release();
        }
    }

    /// <summary>停止当前轮次（owner 校验）。</summary>
    public async Task StopTurnAsync(WebPrincipal owner, long sessionId, CancellationToken ct = default)
    {
        await EnsureOwnerAsync(owner, sessionId, ct);
        if (_slots.TryGetValue(sessionId, out var slot))
        {
            try { slot.TurnCts?.Cancel(); } catch { /* 停轮失败忽略 */ }
            _accessLog.Write("turn_stop", owner.Username, null, $"session={sessionId}");
        }
    }

    /// <summary>
    /// 列出会话目录条目（报告 D6/§3.1/§10.C.2-3 交付物取回）：owner + 固定根 + PathGuard 物理包含三重校验；
    /// 非递归；跳过 reparse point 条目（不展示链接，防经链接浏览到界外）；目录不存在返回空表；
    /// 越权（非 owner/路径逃逸/根外目录）抛 404 异常并落 file_denied 访问日志（§10.A.3 越权尝试）。
    /// </summary>
    public async Task<IReadOnlyList<WebFileEntry>> ListFilesAsync(WebPrincipal owner, long sessionId,
        string? relPath, CancellationToken ct = default)
    {
        var (workspace, full) = await ResolveInWorkspaceAsync(owner, sessionId, relPath, ct);
        if (!Directory.Exists(full))
            return Array.Empty<WebFileEntry>();

        var entries = new List<WebFileEntry>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(full))
        {
            if (IsReparsePoint(entry))
                continue;
            var name = Path.GetFileName(entry);
            var rel = Path.GetRelativePath(workspace, entry).Replace('\\', '/');
            if (Directory.Exists(entry))
            {
                entries.Add(new WebFileEntry(rel, name, IsDir: true, Size: null,
                    new DirectoryInfo(entry).LastWriteTime.ToString("O")));
            }
            else
            {
                var info = new FileInfo(entry);
                entries.Add(new WebFileEntry(rel, name, IsDir: false, Size: info.Length,
                    info.LastWriteTime.ToString("O")));
            }
        }

        return entries
            .OrderByDescending(e => e.IsDir)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(1000)
            .ToArray();
    }

    /// <summary>
    /// 打开会话文件下载流（校验同 <see cref="ListFilesAsync"/>）；文件不存在/是目录返回 null（404）。
    /// 成功落 file_download 访问日志（§10.A.3）。调用方负责随响应释放流。
    /// </summary>
    public async Task<WebFileDownload?> OpenDownloadAsync(WebPrincipal owner, long sessionId,
        string relPath, CancellationToken ct = default)
    {
        var (_, full) = await ResolveInWorkspaceAsync(owner, sessionId, relPath, ct);
        if (!File.Exists(full))
            return null;

        var info = new FileInfo(full);
        FileStream stream;
        try
        {
            stream = new FileStream(full, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        _accessLog.Write("file_download", owner.Username, null,
            $"session={sessionId} path={info.Name} size={info.Length}");
        return new WebFileDownload(info.Name, stream, info.Length);
    }

    /// <summary>
    /// 解析会话目录内的相对路径 → 绝对路径（报告 §10.C.2/3）：
    /// owner 校验（越权=404+日志）→ 会话目录必须位于固定根 WebJobsRoot 之下（§5.3.2）→
    /// 拒 <c>..</c>/绝对路径/冒号（ADS）→ PathGuard 物理解析（junction/symlink 逃逸检出）后须仍在会话目录下。
    /// </summary>
    private async Task<(string Workspace, string Full)> ResolveInWorkspaceAsync(
        WebPrincipal owner, long sessionId, string? relPath, CancellationToken ct)
    {
        string workspace;
        try
        {
            await EnsureOwnerAsync(owner, sessionId, ct);
            workspace = await _store.GetSessionWorkspaceDirAsync(sessionId, ct);
        }
        catch (WebSessionNotFoundException)
        {
            DenyFileAccess(owner, sessionId, relPath);
            throw;
        }

        // §5.3.2：Web 会话目录必须在固定根之下（防库行被改写成任意路径读写通道）
        if (string.IsNullOrWhiteSpace(workspace) || !PathGuard.IsUnder(WebJobsRoot, workspace))
        {
            DenyFileAccess(owner, sessionId, relPath);
            throw new WebSessionNotFoundException();
        }

        var rel = (relPath ?? string.Empty).Trim().Replace('/', '\\').TrimStart('\\');
        if (rel.Contains("..", StringComparison.Ordinal) || rel.Contains(':') || Path.IsPathRooted(rel))
        {
            DenyFileAccess(owner, sessionId, relPath);
            throw new WebFilePathDeniedException();
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(workspace, rel));
        }
        catch
        {
            DenyFileAccess(owner, sessionId, relPath);
            throw new WebFilePathDeniedException();
        }

        if (!PathGuard.IsUnder(workspace, full))
        {
            DenyFileAccess(owner, sessionId, relPath);
            throw new WebFilePathDeniedException();
        }
        return (workspace, full);
    }

    private void DenyFileAccess(WebPrincipal owner, long sessionId, string? relPath)
        => _accessLog.Write("file_denied", owner.Username, null, $"session={sessionId} path={relPath ?? string.Empty}");

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;    // 属性读不了视为不可信，直接跳过
        }
    }

    private async Task EnsureRuntimeAsync(WebPrincipal owner, long sessionId, SessionSlot slot, CancellationToken ct)    {
        if (slot.Runtime is not null)
            return;
        var dir = await _store.GetSessionWorkspaceDirAsync(sessionId, ct);
        if (string.IsNullOrWhiteSpace(dir))
            throw new InvalidOperationException("会话目录未分配");
        var messages = await _store.GetMessagesAsync(sessionId, ct);
        var history = BuildHistory(messages);
        var summary = await _store.GetSessionSummaryAsync(sessionId, ct);
        slot.WorkspaceDir = dir;
        slot.Runtime = await _runtimeFactory.CreateAsync(
            new WebRuntimeRequest(sessionId, owner.UserId.ToString(), owner.Username, dir, history, summary), ct);
    }

    /// <summary>库内消息 → 模型历史（user/assistant 文本对；tool/reasoning 轨迹暂不重建，M3-7 随 UI 富化）。</summary>
    internal static List<ChatMessage> BuildHistory(IReadOnlyList<AgentMessageRow> messages)
    {
        var history = new List<ChatMessage>();
        foreach (var m in messages)
        {
            if (string.IsNullOrEmpty(m.Content))
                continue;
            if (m.Role == "user")
                history.Add(new ChatMessage(ChatRole.User, m.Content));
            else if (m.Role == "assistant")
                history.Add(new ChatMessage(ChatRole.Assistant, m.Content));
        }
        return history;
    }

    private async Task EnsureOwnerAsync(WebPrincipal owner, long sessionId, CancellationToken ct)
    {
        var sessionOwner = await _store.GetSessionOwnerUserIdAsync(sessionId, ct);
        if (!string.Equals(sessionOwner, owner.UserId.ToString(), StringComparison.Ordinal))
            throw new WebSessionNotFoundException();   // 不存在与越权统一 404，不泄露存在性
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (id, slot) in _slots)
        {
            try { slot.TurnCts?.Cancel(); } catch { /* 忽略 */ }
            if (slot.Runtime is not null)
            {
                try { await slot.Runtime.DisposeAsync(); } catch { /* 忽略 */ }
            }
            slot.TurnGate.Dispose();
            _slots.TryRemove(id, out _);
        }
    }
}
