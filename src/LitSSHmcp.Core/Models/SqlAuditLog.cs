namespace LitSSHmcp.Core.Models;

public enum SqlOperation
{
    Query,
    Execute,
    Explain,
    Diagnostics,
    Test
}

public class SqlAuditLog
{
    public long Id { get; set; }
    public string DataSourceId { get; set; } = string.Empty;
    public string DataSourceName { get; set; } = string.Empty;
    public SqlOperation Operation { get; set; }
    public string Sql { get; set; } = string.Empty;
    public CommandStatus Status { get; set; }
    public string? Result { get; set; }
    public long? RowsAffected { get; set; }
    public double? DurationMs { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>事件类型：Exec=查询/写入执行, Gate=写审批/拦截, Probe=测试/诊断/EXPLAIN。</summary>
    public AuditCategory Category { get; set; } = AuditCategory.Exec;

    /// <summary>审批决策说明（Gate 类）：manual-approved / auto-approve / manual-rejected / blocked。</summary>
    public string? Decision { get; set; }

    /// <summary>产生该记录的 MCP 会话 ID（每次启动 MCP 服务生成，用于按会话区分/筛选审计）。</summary>
    public string? SessionId { get; set; }

    /// <summary>产生该记录的 MCP 工具名（如 mysql_query / redis_execute），由请求过滤器自动填充。</summary>
    public string? Tool { get; set; }
}
