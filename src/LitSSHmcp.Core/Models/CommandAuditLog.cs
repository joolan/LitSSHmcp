namespace LitSSHmcp.Core.Models;

public enum CommandStatus
{
    Executed,
    Blocked,
    Approved,
    Rejected,
    Failed
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

    /// <summary>产生该记录的 MCP 会话 ID（每次启动 MCP 服务生成，用于按会话区分/筛选审计）。</summary>
    public string? SessionId { get; set; }

    /// <summary>产生该记录的 MCP 工具名（如 ssh_execute_command / docker_logs），由请求过滤器自动填充。</summary>
    public string? Tool { get; set; }
}