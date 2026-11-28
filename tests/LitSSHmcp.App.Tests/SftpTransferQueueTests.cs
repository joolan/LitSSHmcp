using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>SFTP 传输队列的暂停/取消行为。</summary>
public class SftpTransferQueueTests
{
    private static readonly SshServerConfig Server = new()
    {
        Id = "test",
        Name = "test",
        Host = "127.0.0.1"
    };

    [Fact]
    public async Task Pause_lets_current_file_finish_and_holds_the_rest()
    {
        var ssh = new FakeSsh();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ssh.UploadHandler = async (path, ct) =>
        {
            if (path.EndsWith("a.txt", StringComparison.Ordinal))
            {
                started.TrySetResult();
                await release.Task;
            }
            return new FileTransferResult { Success = true };
        };

        var queue = new TransferQueueViewModel(ssh, Server);
        queue.EnqueueUpload(new[] { Local("a.txt"), Local("b.txt") }, "/tmp");

        await started.Task;
        queue.Pause();
        Assert.True(queue.IsPaused);
        Assert.Equal("继续队列", queue.PauseButtonText);

        release.TrySetResult();
        var a = Item(queue, "a.txt");
        var b = Item(queue, "b.txt");
        await WaitForAsync(() => a.Status.StartsWith("完成", StringComparison.Ordinal));

        Assert.Equal("排队中", b.Status);
        Assert.True(b.CanCancel);
        Assert.Single(ssh.UploadCalls);

        queue.Resume();
        Assert.False(queue.IsPaused);
        await WaitForAsync(() => b.Status.StartsWith("完成", StringComparison.Ordinal));

        Assert.Equal(2, ssh.UploadCalls.Count);
        Assert.False(b.CanCancel);
    }

    [Fact]
    public async Task Cancel_pending_item_removes_it_without_starting_transfer()
    {
        var ssh = new FakeSsh();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ssh.UploadHandler = async (path, ct) =>
        {
            started.TrySetResult();
            await release.Task;
            return new FileTransferResult { Success = true };
        };

        var queue = new TransferQueueViewModel(ssh, Server);
        queue.EnqueueUpload(new[] { Local("a.txt"), Local("b.txt") }, "/tmp");
        await started.Task;

        var b = Item(queue, "b.txt");
        queue.Cancel(b);
        Assert.Equal("已取消", b.Status);
        Assert.False(b.CanCancel);

        release.TrySetResult();
        var a = Item(queue, "a.txt");
        await WaitForAsync(() => a.Status.StartsWith("完成", StringComparison.Ordinal));

        Assert.Equal("已取消", b.Status);
        Assert.Single(ssh.UploadCalls);
    }

    [Fact]
    public async Task Cancel_all_stops_current_and_pending_and_queue_can_restart()
    {
        var ssh = new FakeSsh();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ssh.UploadHandler = async (path, ct) =>
        {
            if (path.EndsWith("a.txt", StringComparison.Ordinal))
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return new FileTransferResult { Success = true };
        };

        var queue = new TransferQueueViewModel(ssh, Server);
        queue.EnqueueUpload(new[] { Local("a.txt"), Local("b.txt") }, "/tmp");
        await started.Task;

        var a = Item(queue, "a.txt");
        var b = Item(queue, "b.txt");
        queue.CancelAll();

        Assert.Equal("取消中…", a.Status);
        Assert.Equal("已取消", b.Status);
        await WaitForAsync(() => a.Status == "已取消");

        // 取消后队列仍可继续使用。
        ssh.UploadHandler = (_, _) => Task.FromResult(new FileTransferResult { Success = true });
        queue.EnqueueUpload(new[] { Local("c.txt") }, "/tmp");
        var c = Item(queue, "c.txt");
        await WaitForAsync(() => c.Status.StartsWith("完成", StringComparison.Ordinal));
    }

    private static LocalFsItem Local(string name) => new() { Name = name, FullName = "C:\\tmp\\" + name };

    private static TransferItem Item(TransferQueueViewModel queue, string name)
        => queue.Items.First(item => item.Name == name);

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
                return;
            await Task.Delay(20);
        }
        Assert.True(predicate(), "等待传输队列状态超时。");
    }

    private sealed class FakeSsh : ISshService
    {
        public Func<string, CancellationToken, Task<FileTransferResult>>? UploadHandler { get; set; }
        public List<string> UploadCalls { get; } = new();

        public Task<FileTransferResult> UploadFileAsync(SshServerConfig server, string localPath, string remotePath,
            IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default)
        {
            lock (UploadCalls)
                UploadCalls.Add(localPath);
            return UploadHandler?.Invoke(localPath, ct)
                ?? Task.FromResult(new FileTransferResult { Success = true });
        }

        public Task<FileTransferResult> DownloadFileAsync(SshServerConfig server, string remotePath, string localPath,
            IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new FileTransferResult { Success = true });

        public Task<bool> RemoteFileExistsAsync(SshServerConfig server, string remotePath, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<BatchTransferResult> UploadBatchAsync(SshServerConfig server, IReadOnlyList<string> localPaths,
            string remoteDirectory, bool recursive, int maxFiles, long maxFileBytes, long maxTotalBytes,
            Func<string, bool>? overwriteResolver = null, CancellationToken ct = default,
            IProgress<BatchTransferFileProgress>? progress = null)
            => Task.FromResult(new BatchTransferResult { Success = true });

        public Task<BatchTransferResult> DownloadBatchAsync(SshServerConfig server, IReadOnlyList<string> remotePaths,
            string localDirectory, bool recursive, int maxFiles, long maxTotalBytes,
            Func<string, bool>? overwriteResolver = null, CancellationToken ct = default,
            IProgress<BatchTransferFileProgress>? progress = null)
            => Task.FromResult(new BatchTransferResult { Success = true });

        public Task<bool> TestConnectionAsync(SshServerConfig server, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ConnectionProbeResult> ProbeConnectionAsync(SshServerConfig server, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<CommandResult> ExecuteCommandAsync(SshServerConfig server, string command, CancellationToken ct = default, int timeoutSeconds = 60)
            => throw new NotSupportedException();

        public Task<CommandResult> ExecuteWithSudoAsync(SshServerConfig server, string command, CancellationToken ct = default, int timeoutSeconds = 60)
            => throw new NotSupportedException();

        public Task<SshShellSession> OpenShellAsync(SshServerConfig server, string terminalType, uint columns, uint rows, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<RemoteFileListResult> ListRemoteFilesAsync(SshServerConfig server, string remotePath, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<CommandResult> ExecuteStreamingAsync(SshServerConfig server, string command,
            Action<string>? onStdout = null, Action<string>? onStderr = null,
            string? stdinText = null, CancellationToken ct = default, int timeoutSeconds = 120)
            => throw new NotSupportedException();

        public Task<RemoteCopyResult> CopyRemoteToRemoteAsync(RemoteCopyRequest request,
            IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
