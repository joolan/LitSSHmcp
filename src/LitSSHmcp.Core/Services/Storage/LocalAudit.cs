using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Storage;

/// <summary>
/// Agent 本地工具审计统一出口（评估报告 §10.A.1：本地工具零审计缺口）。
/// 覆盖 ops_doc_* 写、脚本登记、附件落盘、会话删除、本机终端执行/拒绝等**非 MCP** 事件——
/// 这些操作不经过 MCP 子进程，此前全部游离在哈希链之外（"谁把什么文件提升进共享脚本库"无据可查）。
/// 口径与 MCP 侧 ToolSupport.SafeLogCommandAsync 一致：审计失败静默，绝不阻断业务
/// （宁可丢一条审计也不损坏链或卡死工具调用）。
/// SessionId 取 <see cref="AmbientSessionId"/>（由桌面 Agent 按活动会话设置，缺省 null）；
/// UserId 由 <see cref="AuditLogService"/> 解析（上下文缺省 = "local"，防伪造 §10.A.5）。
/// </summary>
public static class LocalAudit
{
    private static readonly AuditLogService Service = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _ready;

    /// <summary>
    /// 进程级活动会话归因（UI 线程操作的缺省值，如设置页/未在任务中的 UI 动作）。
    /// 由桌面 Agent 按当前显示会话设置；null = 无会话。
    /// </summary>
    private static string? _fallbackSessionId;

    /// <summary>
    /// 运行流内的会话归因（评估报告 §4.1 并行会话）：每轮运行经 <see cref="BeginRunScope"/> 注入，
    /// 随执行流（工具线程/续延）传播且互不串号；空 = 回退进程级缺省。
    /// </summary>
    private static readonly AsyncLocal<string?> RunScopeSessionId = new();

    /// <summary>活动 Agent 会话归因（如 agent-42）；null = 无会话（新建任务尚未发送）。运行流内优先取 <see cref="BeginRunScope"/> 的值。</summary>
    public static string? AmbientSessionId
    {
        get => RunScopeSessionId.Value ?? _fallbackSessionId;
        set => _fallbackSessionId = value;
    }

    /// <summary>
    /// 在本轮运行的执行流上绑定会话归因：作用于本流派生的工具线程/续延，
    /// 不影响其他线程与后续 UI 动作（UI 侧归因继续走 <see cref="AmbientSessionId"/> 的进程级缺省）。
    /// </summary>
    public static IDisposable BeginRunScope(string? sessionId)
    {
        var previous = RunScopeSessionId.Value;
        RunScopeSessionId.Value = sessionId;
        return new RunScope(() => RunScopeSessionId.Value = previous);
    }

    private sealed class RunScope(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;
        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }

    /// <summary>
    /// 测试替身：非 null 时所有记录转投此处，**绝不触碰真实 audit.db**
    /// （各测试程序集经 ModuleInitializer 默认置为空操作；审计测试临时换入捕获器后还原）。
    /// </summary>
    public static Func<CommandAuditLog, Task>? OverrideSink { get; set; }

    /// <summary>写一条本地工具审计。类别固定 Local；<paramref name="decision"/> 仅拒绝/审批语义时传；
    /// <paramref name="sessionId"/> 非空时优先于环境归因（UI 侧动作在其他会话运行期间显式指定，防串号 §4.1）。</summary>
    public static async Task LogAsync(
        string tool,
        string command,
        CommandStatus status,
        string? result = null,
        string? filePath = null,
        string? decision = null,
        int? exitCode = null,
        string? sessionId = null)
    {
        var log = new CommandAuditLog
        {
            ServerId = "local",
            ServerName = "本机",
            Tool = tool,
            Command = command,
            Status = status,
            Result = result,
            FilePath = filePath,
            Decision = decision,
            ExitCode = exitCode,
            Category = AuditCategory.Local,
            SessionId = sessionId ?? AmbientSessionId
        };

        var sink = OverrideSink;
        if (sink is not null)
        {
            try { await sink(log).ConfigureAwait(false); } catch { /* 测试替身失败同样不外抛 */ }
            return;
        }

        try
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_ready)
                {
                    await Service.InitializeAsync().ConfigureAwait(false);
                    _ready = true;
                }
                await Service.LogCommandAsync(log).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }
        catch
        {
            // 与 MCP SafeLog 同口径：链锁超时/磁盘失败等宁可丢这条审计, 不阻断业务、不损坏链
        }
    }
}
