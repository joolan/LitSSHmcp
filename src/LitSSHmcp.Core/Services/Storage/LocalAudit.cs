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

    /// <summary>活动 Agent 会话归因（如 agent-42）；null = 无会话（新建任务尚未发送）。</summary>
    public static string? AmbientSessionId { get; set; }

    /// <summary>
    /// 测试替身：非 null 时所有记录转投此处，**绝不触碰真实 audit.db**
    /// （各测试程序集经 ModuleInitializer 默认置为空操作；审计测试临时换入捕获器后还原）。
    /// </summary>
    public static Func<CommandAuditLog, Task>? OverrideSink { get; set; }

    /// <summary>写一条本地工具审计。类别固定 Local；<paramref name="decision"/> 仅拒绝/审批语义时传。</summary>
    public static async Task LogAsync(
        string tool,
        string command,
        CommandStatus status,
        string? result = null,
        string? filePath = null,
        string? decision = null,
        int? exitCode = null)
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
            SessionId = AmbientSessionId
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
