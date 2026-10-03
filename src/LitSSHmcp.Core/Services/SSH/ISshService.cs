using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.SSH;

public interface ISshService
{
    Task<bool> TestConnectionAsync(SshServerConfig server, CancellationToken ct = default);

    /// <summary>
    /// 连通性探测并返回失败根因（auth / host_key / timeout / network / unknown）。
    /// 只返回 bool 会让"密码错 / 超时 / 主机密钥被换"三者不可分，
    /// 模型无法给出正确处置建议（主机密钥变化还可能是安全事件）。
    /// </summary>
    Task<ConnectionProbeResult> ProbeConnectionAsync(SshServerConfig server, CancellationToken ct = default);

    Task<CommandResult> ExecuteCommandAsync(SshServerConfig server, string command, CancellationToken ct = default, int timeoutSeconds = 60);
    Task<CommandResult> ExecuteWithSudoAsync(SshServerConfig server, string command, CancellationToken ct = default);
    Task<FileTransferResult> UploadFileAsync(SshServerConfig server, string localPath, string remotePath, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default);
    Task<FileTransferResult> DownloadFileAsync(SshServerConfig server, string remotePath, string localPath, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default);
    Task<RemoteFileListResult> ListRemoteFilesAsync(SshServerConfig server, string remotePath, CancellationToken ct = default);
}

/// <summary>连通性探测结果。</summary>
public class ConnectionProbeResult
{
    public bool Success { get; set; }

    /// <summary>失败分类: auth(认证失败) / host_key(主机密钥不匹配, 可能是安全事件) / timeout(超时) / network(网络不可达) / unknown。</summary>
    public string ErrorKind { get; set; } = "unknown";

    public string Error { get; set; } = string.Empty;

    public TimeSpan Duration { get; set; }
}

public class CommandResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// 失败分类（会被工具层映射为 status，让模型区分“可重试”与“不可重试”）。
    /// rate_limited / timeout / auth / host_key / network / unknown；null 表示命令本身执行失败（exit != 0）。
    /// </summary>
    public string? ErrorKind { get; set; }

    /// <summary>
    /// 本次提权实际使用的机制（不敏感）：<c>direct</c>(未提权/已是目标用户) / <c>sudo</c> / <c>su</c>
    /// / <c>auto:sudo</c> / <c>auto:su</c> / <c>auto:failed</c>。仅提权工具会透出给客户端，便于排障与透明说明。
    /// </summary>
    public string? Escalation { get; set; }

    /// <summary>是否因被限流而未执行（模型据此应退避重试，而非判定命令失败）。</summary>
    public bool RateLimited => ErrorKind == "rate_limited";
}

public class FileTransferResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public long BytesTransferred { get; set; }
    public TimeSpan Duration { get; set; }
}

public class FileTransferProgress
{
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    public double Percentage => TotalBytes > 0 ? (double)BytesTransferred / TotalBytes * 100 : 0;
}

public class RemoteFileInfo
{
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public bool IsDirectory { get; set; }
    public bool IsSymbolicLink { get; set; }
}

/// <summary>
/// 远程列目录结果。刻意区分"目录为空"与"列目录失败"——
/// 此前失败一律返回空数组，凭据错/权限不足/断网都会被模型读成"这个目录是空的"。
/// </summary>
public class RemoteFileListResult
{
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public string ErrorKind { get; set; } = "unknown";
    public RemoteFileInfo[] Files { get; set; } = Array.Empty<RemoteFileInfo>();

    /// <summary>true=目录条目过多已截断。</summary>
    public bool Truncated { get; set; }
}