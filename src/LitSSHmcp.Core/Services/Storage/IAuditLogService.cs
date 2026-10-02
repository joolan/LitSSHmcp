using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Storage;

public interface IAuditLogService
{
    Task InitializeAsync();

    /// <summary>
    /// 当前 MCP 会话 ID（每次启动 MCP 服务生成）。写入审计时若记录本身没带 SessionId，则自动填该值，
    /// 从而无需每个工具手动传参即可按会话区分日志。
    /// </summary>
    string? SessionId { get; set; }

    Task LogCommandAsync(CommandAuditLog log);

    /// <param name="offset">翻页偏移（配合 limit 一起用），此前缺失导致只能靠 limit 硬翻。</param>
    /// <param name="sessionId">按 MCP 会话 ID 过滤（可选）。</param>
    /// <param name="tool">按产生记录的 MCP 工具名过滤（可选）。</param>
    Task<CommandAuditLog[]> GetLogsAsync(string? serverId = null, int limit = 100, string? keyword = null, bool includeHistory = false, int offset = 0, string? sessionId = null, string? tool = null);
    Task LogSqlAsync(SqlAuditLog log);
    Task<SqlAuditLog[]> GetSqlLogsAsync(string? dataSourceId = null, int limit = 100, string? keyword = null, bool includeHistory = false, int offset = 0, string? sessionId = null, string? tool = null);

    /// <summary>记录/更新一次 MCP 会话（含客户端名称/版本），用于把 SessionId 映射到具体客户端。</summary>
    Task RecordSessionAsync(AuditSession session);

    /// <summary>列出最近的 MCP 会话（按最近活动倒序）。</summary>
    Task<AuditSession[]> GetSessionsAsync(int limit = 50);

    /// <summary>校验审计哈希链：完整性 + 是否被篡改。链引用的记录可来自活动表或历史归档表。</summary>
    Task<AuditVerifyResult> VerifyChainAsync(CancellationToken ct = default);
}
