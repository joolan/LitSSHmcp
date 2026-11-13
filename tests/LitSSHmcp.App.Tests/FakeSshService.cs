using System.IO;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.App.Tests;

internal sealed class FakeSshService : ISshService
{
    public Func<string, string>? OutputResolver { get; set; }
    public Func<string, string>? SudoOutputResolver { get; set; }
    public List<string> Commands { get; } = new();
    public List<string> CommandLines { get; } = new();
    public string? UploadedRemotePath { get; private set; }
    public byte[]? UploadedBytes { get; private set; }

    public Task<bool> TestConnectionAsync(SshServerConfig server, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<ConnectionProbeResult> ProbeConnectionAsync(SshServerConfig server, CancellationToken ct = default)
        => Task.FromResult(new ConnectionProbeResult { Success = true });

    public Task<CommandResult> ExecuteCommandAsync(
        SshServerConfig server,
        string command,
        CancellationToken ct = default,
        int timeoutSeconds = 60)
        => ExecuteRecordedAsync(command);

    public Task<CommandResult> ExecuteWithSudoAsync(
        SshServerConfig server,
        string command,
        CancellationToken ct = default)
        => ExecuteRecordedAsync(command, SudoOutputResolver ?? OutputResolver);

    public Task<SshShellSession> OpenShellAsync(
        SshServerConfig server,
        string terminalType,
        uint columns,
        uint rows,
        CancellationToken ct = default)
        => Task.FromException<SshShellSession>(new NotSupportedException());

    public Task<FileTransferResult> UploadFileAsync(
        SshServerConfig server,
        string localPath,
        string remotePath,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        UploadedRemotePath = remotePath;
        UploadedBytes = File.Exists(localPath) ? File.ReadAllBytes(localPath) : Array.Empty<byte>();
        return Task.FromResult(new FileTransferResult { Success = true, Message = "ok" });
    }

    public Task<FileTransferResult> DownloadFileAsync(
        SshServerConfig server,
        string remotePath,
        string localPath,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken ct = default)
        => Task.FromResult(new FileTransferResult { Success = false, Message = "not supported" });

    public Task<RemoteFileListResult> ListRemoteFilesAsync(
        SshServerConfig server,
        string remotePath,
        CancellationToken ct = default)
        => Task.FromResult(new RemoteFileListResult { Success = true });

    public Task<bool> RemoteFileExistsAsync(
        SshServerConfig server,
        string remotePath,
        CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<BatchTransferResult> DownloadBatchAsync(
        SshServerConfig server,
        IReadOnlyList<string> remotePaths,
        string localDirectory,
        bool recursive,
        int maxFiles,
        long maxTotalBytes,
        Func<string, bool>? overwriteResolver = null,
        CancellationToken ct = default,
        IProgress<BatchTransferFileProgress>? progress = null)
        => Task.FromResult(new BatchTransferResult { Success = true });

    public Task<BatchTransferResult> UploadBatchAsync(
        SshServerConfig server,
        IReadOnlyList<string> localPaths,
        string remoteDirectory,
        bool recursive,
        int maxFiles,
        long maxFileBytes,
        long maxTotalBytes,
        Func<string, bool>? overwriteResolver = null,
        CancellationToken ct = default,
        IProgress<BatchTransferFileProgress>? progress = null)
        => Task.FromResult(new BatchTransferResult { Success = true });

    public Task<CommandResult> ExecuteStreamingAsync(
        SshServerConfig server,
        string command,
        Action<string>? onStdout = null,
        Action<string>? onStderr = null,
        string? stdinText = null,
        CancellationToken ct = default,
        int timeoutSeconds = 120)
        => Task.FromResult(Result(OutputResolver?.Invoke(command) ?? string.Empty));

    public Task<RemoteCopyResult> CopyRemoteToRemoteAsync(
        RemoteCopyRequest request,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken ct = default)
        => Task.FromResult(new RemoteCopyResult { Success = false, Error = "not supported" });

    private Task<CommandResult> ExecuteRecordedAsync(string command, Func<string, string>? resolver = null)
    {
        CommandLines.Add(command);
        return Task.FromResult(Result((resolver ?? OutputResolver)?.Invoke(command) ?? string.Empty));
    }

    private CommandResult Result(string output)
    {
        Commands.Add(output);
        return new CommandResult { Success = true, Output = output, ExitCode = 0 };
    }
}
