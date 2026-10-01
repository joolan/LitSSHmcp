using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Storage;

public interface IAuditLogService
{
    Task InitializeAsync();
    Task LogCommandAsync(CommandAuditLog log);
    Task<CommandAuditLog[]> GetLogsAsync(string? serverId = null, int limit = 100, string? keyword = null, bool includeHistory = false);
    Task LogSqlAsync(SqlAuditLog log);
    Task<SqlAuditLog[]> GetSqlLogsAsync(string? dataSourceId = null, int limit = 100, string? keyword = null, bool includeHistory = false);

    /// <summary>校验审计哈希链：完整性 + 是否被篡改。链引用的记录可来自活动表或历史归档表。</summary>
    Task<AuditVerifyResult> VerifyChainAsync(CancellationToken ct = default);
}
