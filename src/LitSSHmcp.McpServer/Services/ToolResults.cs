using LitSSHmcp.Core.Models;

namespace LitSSHmcp.McpServer.Services;

/// <summary>
/// 工具的结构化输出 DTO（配合 <c>[McpServerTool(UseStructuredContent = true, OutputSchemaType = ...)]</c>）。
/// SDK 会同时产出 <c>structuredContent</c>（按 DTO 生成的 outputSchema）与等价的 text(JSON)，兼容旧客户端。
/// 字段同时覆盖成功与失败：失败时 <see cref="Success"/>=false 且带 <see cref="Status"/>/<see cref="Error"/>。
/// </summary>

public sealed class QueryResultDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? DatasourceId { get; set; }
    public string? Name { get; set; }
    public string[]? Columns { get; set; }
    public List<Dictionary<string, object?>>? Rows { get; set; }
    public long RowCount { get; set; }
    public bool Truncated { get; set; }
    public double DurationMs { get; set; }

    public static QueryResultDto Fail(string status, string error) =>
        new() { Success = false, Status = status, Error = error };
}

public sealed class ExecuteResultDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? DatasourceId { get; set; }
    public string? Name { get; set; }
    public long RowsAffected { get; set; }
    public double DurationMs { get; set; }

    public static ExecuteResultDto Fail(string status, string error) =>
        new() { Success = false, Status = status, Error = error };
}

public sealed class DiagnosticsResultDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? DatasourceId { get; set; }
    public string? Name { get; set; }
    public string? Summary { get; set; }
    public Dictionary<string, object?>? Data { get; set; }
    public double DurationMs { get; set; }

    public static DiagnosticsResultDto Fail(string status, string error) =>
        new() { Success = false, Status = status, Error = error };
}

public sealed class TestConnectionResultDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? DatasourceId { get; set; }
    public string? Name { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; }
    public string? AccessMode { get; set; }
    public string? ViaTunnelServer { get; set; }
    public string? Version { get; set; }
    public double DurationMs { get; set; }

    public static TestConnectionResultDto Fail(string status, string error) =>
        new() { Success = false, Status = status, Error = error };
}

public sealed class SshServerSummaryDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Username { get; set; } = string.Empty;
    public string AuthType { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();
    public DateTime? LastConnectedAt { get; set; }
}

public sealed class SshServerListDto
{
    public bool Success { get; set; } = true;
    public int Count { get; set; }
    public List<SshServerSummaryDto> Servers { get; set; } = new();
}

public sealed class ServerStatusDto
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Host { get; set; }

    /// <summary>连接状态：Connected / Disconnected。</summary>
    public string? Status { get; set; }

    public static ServerStatusDto Fail(string error) => new() { Success = false, Error = error };
}

public sealed class SshTestConnectionDto
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ServerId { get; set; }
    public string? Name { get; set; }

    public static SshTestConnectionDto Fail(string error) => new() { Success = false, Error = error };
}

public sealed class DiscoverResultDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public DiscoveryResult? Result { get; set; }

    public static DiscoverResultDto Fail(string status, string error) =>
        new() { Success = false, Status = status, Error = error };
}

public sealed class CommandResultDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Reason { get; set; }
    public string? Error { get; set; }
    public string? Command { get; set; }
    public string? Output { get; set; }
    public int ExitCode { get; set; }
    public double DurationMs { get; set; }

    public static CommandResultDto Fail(string status, string error, string? reason = null, string? command = null) =>
        new() { Success = false, Status = status, Error = error, Reason = reason, Command = command };
}

public sealed class SudoStatusDto
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ServerId { get; set; }
    public string? ServerName { get; set; }
    public string? SudoType { get; set; }
    public string? SudoUsername { get; set; }
    public bool IsConfigured { get; set; }
    public string? Description { get; set; }

    public static SudoStatusDto Fail(string error) => new() { Success = false, Error = error };
}

public sealed class FileTransferResultDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Reason { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public long BytesTransferred { get; set; }
    public double DurationMs { get; set; }
    public string[]? AllowedLocalPaths { get; set; }
    public string[]? AllowedRemotePaths { get; set; }

    public static FileTransferResultDto Fail(string status, string error, string? reason = null,
        string[]? allowedLocalPaths = null, string[]? allowedRemotePaths = null) =>
        new()
        {
            Success = false,
            Status = status,
            Error = error,
            Reason = reason,
            AllowedLocalPaths = allowedLocalPaths,
            AllowedRemotePaths = allowedRemotePaths
        };
}

public sealed class RemoteFileDto
{
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public DateTime LastModified { get; set; }
    public bool IsDirectory { get; set; }
    public bool IsSymbolicLink { get; set; }
}

public sealed class RemoteFileListDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? Path { get; set; }
    public List<RemoteFileDto> Files { get; set; } = new();

    public static RemoteFileListDto Fail(string status, string error) =>
        new() { Success = false, Status = status, Error = error };
}

public sealed class CommandHistoryDto
{
    public bool Success { get; set; } = true;
    public int Count { get; set; }
    public List<CommandAuditLog> Records { get; set; } = new();
}

public sealed class SqlHistoryDto
{
    public bool Success { get; set; } = true;
    public int Count { get; set; }
    public List<SqlAuditLog> Records { get; set; } = new();
}

public sealed class DatasourceSummaryDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? DefaultDatabase { get; set; }
    public string AccessMode { get; set; } = string.Empty;
    public string? TunnelServerId { get; set; }
    public string? TunnelServer { get; set; }
    public string? Description { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string[] AccessibleFromSshServers { get; set; } = Array.Empty<string>();
    public string[] ConnectedApplications { get; set; } = Array.Empty<string>();
}

public sealed class DatasourceListDto
{
    public bool Success { get; set; } = true;
    public int Count { get; set; }
    public List<DatasourceSummaryDto> DataSources { get; set; } = new();
}

public sealed class HealthCheckItemDto
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

public sealed class HealthCheckDto
{
    public bool Success { get; set; }
    public List<HealthCheckItemDto> Checks { get; set; } = new();
}

public sealed class RedisCommandResultDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? DatasourceId { get; set; }
    public string? Name { get; set; }
    public string[]? Command { get; set; }

    /// <summary>命令结果的 JSON 文本（标量为 JSON 字面量如 <c>"hello"</c>/<c>42</c>，数组为 JSON 数组）。</summary>
    public string? Result { get; set; }

    public bool Truncated { get; set; }
    public double DurationMs { get; set; }
    public string? AccessMode { get; set; }
    public string? ViaTunnelServer { get; set; }

    public static RedisCommandResultDto Fail(string status, string error) =>
        new() { Success = false, Status = status, Error = error };
}
