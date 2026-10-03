namespace LitSSHmcp.Core.Models;

public enum CommandStatus
{
    Executed,
    Blocked,
    Approved,
    Rejected,
    Failed
}

/// <summary>审计事件类型：既覆盖"操作日志"（执行/探测/列表），也覆盖"审计日志"（审批/拦截）。</summary>
public enum AuditCategory
{
    /// <summary>命令/SQL 的实际执行（含普通只读命令）。</summary>
    Exec,
    /// <summary>审批/拦截决策（人工或自动）：Approved / Rejected / Blocked / Timeout / Unavailable。</summary>
    Gate,
    /// <summary>只读探测：连接测试、状态探测、列目录等。</summary>
    Probe,
    /// <summary>列表/元数据：列服务器、列数据源、看拓扑等。</summary>
    Meta,
    /// <summary>文件上传/下载。</summary>
    Transfer
}

public class CommandAuditLog
{
    public long Id { get; set; }
    public string ServerId { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string? Result { get; set; }
    public CommandStatus Status { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int? ExitCode { get; set; }
    public bool IsFileTransfer { get; set; }
    public string? FilePath { get; set; }
    public long? FileSize { get; set; }

    /// <summary>事件类型（操作日志/审计日志的分类，便于过滤）。</summary>
    public AuditCategory Category { get; set; } = AuditCategory.Exec;

    /// <summary>审批决策说明（Gate 类）：manual-approved / manual-rejected / auto-approve / auto-reject / timeout / unavailable / blocked。</summary>
    public string? Decision { get; set; }

    /// <summary>产生该记录的 MCP 会话 ID（每次启动 MCP 服务生成，用于按会话区分/筛选审计）。</summary>
    public string? SessionId { get; set; }

    /// <summary>产生该记录的 MCP 工具名（如 ssh_execute_command / docker_logs），由请求过滤器自动填充。</summary>
    public string? Tool { get; set; }
}