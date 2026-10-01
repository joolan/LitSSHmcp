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
}
