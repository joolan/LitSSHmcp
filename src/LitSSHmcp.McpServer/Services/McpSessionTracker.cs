using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Services;

/// <summary>
/// 当前 MCP 会话的运行时信息：会话 ID（启动时生成）+ 客户端名称/版本（initialize 后从请求上下文获取）。
/// 进程级单例；stdio 模式下每个客户端连接/重连都是新进程，故对应一个独立会话。
/// </summary>
public sealed class McpSessionTracker
{
    private readonly object _gate = new();
    private string? _clientName;
    private string? _clientVersion;
    private bool _recorded;
    private DateTime _lastRecordedAt = DateTime.MinValue;

    public McpSessionTracker(string sessionId)
    {
        SessionId = sessionId;
        StartedAt = DateTime.UtcNow;
    }

    public string SessionId { get; }
    public DateTime StartedAt { get; }

    public string? ClientName
    {
        get { lock (_gate) return _clientName; }
    }

    public string? ClientVersion
    {
        get { lock (_gate) return _clientVersion; }
    }

    /// <summary>
    /// 记录客户端信息。返回是否需要写库（首次 / 客户端信息变化 / 距上次写库超过刷新间隔），
    /// 避免每次工具调用都写一次 Sessions 表。
    /// </summary>
    public bool Observe(string? clientName, string? clientVersion)
    {
        lock (_gate)
        {
            var changed = !string.Equals(_clientName, clientName) || !string.Equals(_clientVersion, clientVersion);
            _clientName = clientName;
            _clientVersion = clientVersion;

            var now = DateTime.UtcNow;
            if (changed || !_recorded || now - _lastRecordedAt > TimeSpan.FromMinutes(5))
            {
                _recorded = true;
                _lastRecordedAt = now;
                return true;
            }

            return false;
        }
    }

    public AuditSession Snapshot()
    {
        lock (_gate)
        {
            return new AuditSession
            {
                SessionId = SessionId,
                ClientName = _clientName,
                ClientVersion = _clientVersion,
                StartedAt = StartedAt,
                LastSeenAt = DateTime.UtcNow
            };
        }
    }
}

/// <summary>
/// 工具调用请求过滤器：在调用工具前，从 <c>context.Server.ClientInfo</c>（initialize 握手后已填充）
/// 取客户端名称/版本写入会话表，使审计的 SessionId 能映射到具体 AI 客户端。
/// </summary>
public static class McpSessionFilter
{
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CreateFilter()
    {
        McpRequestFilter<CallToolRequestParams, CallToolResult> filter = next => async (context, ct) =>
        {
            try
            {
                var tracker = context.Services?.GetService(typeof(McpSessionTracker)) as McpSessionTracker;
                var audit = context.Services?.GetService(typeof(IAuditLogService)) as IAuditLogService;
                if (tracker is not null)
                {
                    var info = context.Server?.ClientInfo;
                    if (tracker.Observe(info?.Name, info?.Version) && audit is not null)
                        await audit.RecordSessionAsync(tracker.Snapshot());
                }
            }
            catch
            {
                // 会话记录失败绝不能影响工具调用
            }

            // 把当前工具名放进审计上下文，工具内部写审计时自动带上（无需各工具手动传参）
            var previousTool = AuditContext.CurrentTool;
            AuditContext.CurrentTool = context.Params?.Name;
            try
            {
                return await next(context, ct);
            }
            finally
            {
                AuditContext.CurrentTool = previousTool;
            }
        };

        return filter;
    }
}
