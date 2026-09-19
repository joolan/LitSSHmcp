using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.SSH;

public interface ISshService
{
    Task<bool> TestConnectionAsync(SshServerConfig server, CancellationToken ct = default);
    Task<CommandResult> ExecuteCommandAsync(SshServerConfig server, string command, CancellationToken ct = default);
    Task<CommandResult> ExecuteWithSudoAsync(SshServerConfig server, string command, CancellationToken ct = default);
    Task<FileTransferResult> UploadFileAsync(SshServerConfig server, string localPath, string remotePath, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default);
    Task<FileTransferResult> DownloadFileAsync(SshServerConfig server, string remotePath, string localPath, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default);
    Task<RemoteFileInfo[]> ListRemoteFilesAsync(SshServerConfig server, string remotePath, CancellationToken ct = default);
}

public class CommandResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public TimeSpan Duration { get; set; }
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