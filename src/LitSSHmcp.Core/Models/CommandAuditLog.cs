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
}