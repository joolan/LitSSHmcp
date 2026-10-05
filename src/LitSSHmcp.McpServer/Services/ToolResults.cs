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

    /// <summary>连接状态：connected / disconnected / auth_failed / host_key_mismatch / timeout / connection_error。</summary>
    public string? Status { get; set; }

    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Host { get; set; }

    /// <summary>失败分类: auth / host_key / timeout / network。</summary>
    public string? ErrorKind { get; set; }

    public double DurationMs { get; set; }

    public static ServerStatusDto Fail(string error) => new() { Success = false, Error = error };

    /// <summary>带 status 的失败：如 server_not_found / server_disabled，模型可据此判断能否重试。</summary>
    public static ServerStatusDto Fail(string status, string error) =>
        new() { Success = false, Status = status, Error = error };
}

public sealed class SshTestConnectionDto
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Status { get; set; }
    public string? ErrorKind { get; set; }
    public string? ServerId { get; set; }
    public string? Name { get; set; }
    public string? Host { get; set; }
    public double DurationMs { get; set; }

    public static SshTestConnectionDto Fail(string status, string error, string? errorKind = null) =>
        new() { Success = false, Status = status, Error = error, ErrorKind = errorKind };
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
    public string? ServerId { get; set; }
    public string? ServerName { get; set; }
    public string? Host { get; set; }
    public string? Command { get; set; }
    public string? Output { get; set; }
    public int ExitCode { get; set; }

    /// <summary>输出是否被截断（SSH 命令输出没有行数上限，必须给模型一个"还有更多"的信号）。</summary>
    public bool Truncated { get; set; }

    /// <summary>截断前的原始输出字符数。</summary>
    public long OutputChars { get; set; }

    public double DurationMs { get; set; }

    /// <summary>本次提权实际使用的机制（仅 ssh_execute_sudo 会填充）：direct / sudo / su / auto:sudo / auto:su / auto:failed。</summary>
    public string? Escalation { get; set; }

    public static CommandResultDto Fail(string status, string error, string? reason = null, string? command = null,
        string? serverId = null, string? serverName = null, string? host = null) =>
        new()
        {
            Success = false,
            Status = status,
            Error = error,
            Reason = reason,
            Command = command,
            ServerId = serverId,
            ServerName = serverName,
            Host = host
        };
}

public sealed class SudoStatusDto
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ServerId { get; set; }
    public string? ServerName { get; set; }
    public string? Host { get; set; }
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
    public string? ServerId { get; set; }
    public string? ServerName { get; set; }
    public string? Host { get; set; }
    public string? Message { get; set; }
    public long BytesTransferred { get; set; }
    public double DurationMs { get; set; }
    public string[]? AllowedLocalPaths { get; set; }
    public string[]? AllowedRemotePaths { get; set; }

    public static FileTransferResultDto Fail(string status, string error, string? reason = null,
        string[]? allowedLocalPaths = null, string[]? allowedRemotePaths = null,
        string? serverId = null, string? serverName = null, string? host = null) =>
        new()
        {
            Success = false,
            Status = status,
            Error = error,
            Reason = reason,
            AllowedLocalPaths = allowedLocalPaths,
            AllowedRemotePaths = allowedRemotePaths,
            ServerId = serverId,
            ServerName = serverName,
            Host = host
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

public sealed class BatchFileDto
{
    public string? RemotePath { get; set; }
    public string? LocalPath { get; set; }
    public bool Success { get; set; }
    public string Size { get; set; } = string.Empty;
    public string? Error { get; set; }
}

/// <summary>批量/目录下载结果（一条 SFTP 连接下载多个文件）。</summary>
public sealed class BatchTransferResultDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? ServerId { get; set; }
    public string? ServerName { get; set; }
    public string? Host { get; set; }
    public string? LocalDirectory { get; set; }
    public string? RemoteDirectory { get; set; }
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public bool Truncated { get; set; }
    public long TotalBytes { get; set; }
    public double DurationMs { get; set; }
    public List<BatchFileDto> Files { get; set; } = new();

    public static BatchTransferResultDto Fail(string status, string error,
        string? serverId = null, string? serverName = null, string? host = null) =>
        new() { Success = false, Status = status, Error = error, ServerId = serverId, ServerName = serverName, Host = host };
}

public sealed class RemoteFileListDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? ServerId { get; set; }
    public string? ServerName { get; set; }
    public string? Host { get; set; }
    public string? Path { get; set; }
    public int Count { get; set; }

    /// <summary>true=目录条目过多已截断，需要更精确的路径再查。</summary>
    public bool Truncated { get; set; }

    public List<RemoteFileDto> Files { get; set; } = new();

    public static RemoteFileListDto Fail(string status, string error,
        string? serverId = null, string? serverName = null, string? host = null) =>
        new() { Success = false, Status = status, Error = error, ServerId = serverId, ServerName = serverName, Host = host };
}

public sealed class CommandHistoryDto
{
    public bool Success { get; set; } = true;
    public string? Status { get; set; }
    public string? Error { get; set; }
    public int Count { get; set; }

    /// <summary>true=已达到 limit 上限，还有更早的记录（配合 offset 翻页）。</summary>
    public bool HasMore { get; set; }

    public List<CommandAuditLog> Records { get; set; } = new();

    public static CommandHistoryDto Fail(string status, string error) =>
        new() { Success = false, Status = status, Error = error };
}

public sealed class SqlHistoryDto
{
    public bool Success { get; set; } = true;
    public string? Status { get; set; }
    public string? Error { get; set; }
    public int Count { get; set; }

    /// <summary>true=已达到 limit 上限，还有更早的记录（配合 offset 翻页）。</summary>
    public bool HasMore { get; set; }

    public List<SqlAuditLog> Records { get; set; } = new();

    public static SqlHistoryDto Fail(string status, string error) =>
        new() { Success = false, Status = status, Error = error };
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

    /// <summary>当前 MCP 会话 ID（每次启动服务生成；审计记录据此区分会话）。</summary>
    public string? SessionId { get; set; }

    /// <summary>当前会话的客户端名称（initialize 握手后由服务端获取，首次工具调用前可能为空）。</summary>
    public string? ClientName { get; set; }

    public string? ClientVersion { get; set; }

    public List<HealthCheckItemDto> Checks { get; set; } = new();
}

public sealed class SessionListDto
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int Count { get; set; }
    public List<AuditSession> Sessions { get; set; } = new();

    public static SessionListDto Fail(string error) => new() { Success = false, Error = error };
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

/// <summary>
/// 领域工具（docker_* / service_* / log_* / java_*）的通用文本结果。
/// 统一承载安全链路返回的 status/error 与截断信号。
/// </summary>
public sealed class RemoteCommandResultDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }

    /// <summary>结果来源目标（回声，便于确认没有操作错机器）。</summary>
    public string? ServerId { get; set; }
    public string? ServerName { get; set; }
    public string? Host { get; set; }

    public string? Output { get; set; }
    public bool Truncated { get; set; }
    public long OutputChars { get; set; }
    public int ExitCode { get; set; }
    public double DurationMs { get; set; }

    public static RemoteCommandResultDto From(GuardedCommandOutcome outcome) => new()
    {
        Success = outcome.Success,
        Status = outcome.Status,
        Error = outcome.Error,
        ServerId = outcome.ServerId,
        ServerName = outcome.ServerName,
        Host = outcome.ServerHost,
        Output = outcome.Output,
        Truncated = outcome.Truncated,
        OutputChars = outcome.OutputChars,
        ExitCode = outcome.ExitCode,
        DurationMs = outcome.DurationMs
    };
}

public sealed class DockerContainerDto
{
    public string? Id { get; set; }
    public string? Names { get; set; }
    public string? Image { get; set; }
    public string? Status { get; set; }
    public string? State { get; set; }
    public string? Ports { get; set; }
}

public sealed class DockerPsDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? ServerId { get; set; }
    public string? ServerName { get; set; }
    public string? Host { get; set; }
    public int Count { get; set; }
    public bool Truncated { get; set; }
    public List<DockerContainerDto> Containers { get; set; } = new();

    public static DockerPsDto Fail(string status, string error) =>
        new() { Success = false, Status = status, Error = error };
}

public sealed class JavaProcessDto
{
    public int Pid { get; set; }
    public string? Elapsed { get; set; }
    public string? Cpu { get; set; }
    public string? Mem { get; set; }
    public string? Command { get; set; }
}

public sealed class JavaProcessListDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? ServerId { get; set; }
    public string? ServerName { get; set; }
    public string? Host { get; set; }
    public int Count { get; set; }
    public List<JavaProcessDto> Processes { get; set; } = new();

    public static JavaProcessListDto Fail(string status, string error) =>
        new() { Success = false, Status = status, Error = error };
}

public sealed class LogResultDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? ServerId { get; set; }
    public string? ServerName { get; set; }
    public string? Host { get; set; }
    public string? Path { get; set; }
    public string? Pattern { get; set; }
    public int Count { get; set; }
    public bool Truncated { get; set; }
    public List<string> Lines { get; set; } = new();

    public static LogResultDto Fail(string status, string error,
        string? serverId = null, string? serverName = null, string? host = null) =>
        new() { Success = false, Status = status, Error = error, ServerId = serverId, ServerName = serverName, Host = host };
}

public sealed class LogFileDto
{
    public string Path { get; set; } = string.Empty;
    public DateTimeOffset? ModifiedAt { get; set; }
}

public sealed class LogFileListDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? ServerId { get; set; }
    public string? ServerName { get; set; }
    public string? Host { get; set; }
    public int Count { get; set; }
    public bool Truncated { get; set; }
    public List<LogFileDto> Files { get; set; } = new();

    public static LogFileListDto Fail(string status, string error,
        string? serverId = null, string? serverName = null, string? host = null) =>
        new() { Success = false, Status = status, Error = error, ServerId = serverId, ServerName = serverName, Host = host };
}

public sealed class AppInfoDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Type { get; set; }
    public string? ContainerName { get; set; }
    public int? Port { get; set; }
}

public sealed class AppServerHealthDto
{
    public string? ServerId { get; set; }
    public string? ServerName { get; set; }
    public string? Host { get; set; }
    public bool Reachable { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? Output { get; set; }
    public bool Truncated { get; set; }
}

public sealed class AppDatasourceHealthDto
{
    public string? DatasourceId { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public bool Reachable { get; set; }
    public string? Status { get; set; }
    public string? Version { get; set; }
    public string? AccessMode { get; set; }
    public string? ViaTunnelServer { get; set; }
    public string? Summary { get; set; }
    public string? Error { get; set; }
}

public sealed class AppHealthSnapshotDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public AppInfoDto? Application { get; set; }
    public List<AppServerHealthDto> Servers { get; set; } = new();
    public List<AppDatasourceHealthDto> Datasources { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    public static AppHealthSnapshotDto Fail(string status, string error) =>
        new() { Success = false, Status = status, Error = error };
}

public sealed class SnapshotEventDto
{
    public string? Timestamp { get; set; }
    public string? Kind { get; set; }
    public string? Collector { get; set; }
    public string? Message { get; set; }
}

public sealed class SnapshotSummaryDto
{
    public long Id { get; set; }
    public string? State { get; set; }
    public string? CreatedAt { get; set; }
    public string? CompletedAt { get; set; }
    public double DurationMs { get; set; }
    public string? Error { get; set; }
}

public sealed class SnapshotDto
{
    public bool Success { get; set; }

    /// <summary>ok / succeeded / failed / snapshot_in_progress / snapshot_not_found / server_not_found / server_ambiguous / server_disabled。</summary>
    public string? Status { get; set; }
    public string? Error { get; set; }

    /// <summary>给调用方的下一步提示（如"进行中请稍后查询"、"从未生成请刷新"）。</summary>
    public string? Hint { get; set; }

    public string? ServerId { get; set; }
    public string? ServerName { get; set; }
    public string? Host { get; set; }

    public long? SnapshotId { get; set; }

    /// <summary>running / succeeded / failed。</summary>
    public string? State { get; set; }
    public string? CreatedAt { get; set; }
    public string? CompletedAt { get; set; }
    public double? DurationMs { get; set; }
    public string? Escalation { get; set; }
    public int? CollectorVersion { get; set; }

    /// <summary>采集数据: { collectorVersion, elevated, sections: { resource/portmap/nginx_tls/systemd: { status, durationMs, error, note, data } } }。</summary>
    public Dictionary<string, object?>? Data { get; set; }

    public List<SnapshotEventDto>? Events { get; set; }

    /// <summary>最近若干份快照的轻量摘要（含本次），用于一眼看历史/后续趋势。</summary>
    public List<SnapshotSummaryDto>? Recent { get; set; }

    public static SnapshotDto Fail(string status, string error) =>
        new() { Success = false, Status = status, Error = error };
}

