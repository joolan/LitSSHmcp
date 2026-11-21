using System.Collections.ObjectModel;
using System.IO;
using FluentFTP;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.FileTransfer;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public sealed class FtpTransferQueueViewModel
{
    private readonly Func<CancellationToken, Task<FtpSession>> _sessionFactory;
    private readonly FileTransferSiteConfig _site;
    private readonly Action _afterTransfer;
    private readonly IAuditLogService _audit;
    private readonly IUserDialogService _dialogs;
    private readonly Queue<Job> _pending = new();
    private readonly List<Job> _failed = new();
    private bool _running;
    private bool _paused;
    private CancellationTokenSource? _cts;

    public FtpTransferQueueViewModel(
        Func<CancellationToken, Task<FtpSession>> sessionFactory,
        FileTransferSiteConfig site,
        Action afterTransfer,
        IAuditLogService audit,
        IUserDialogService dialogs)
    {
        _sessionFactory = sessionFactory;
        _site = site;
        _afterTransfer = afterTransfer;
        _audit = audit;
        _dialogs = dialogs;
    }

    public ObservableCollection<TransferItem> Items { get; } = new();
    public bool IsPaused => _paused;

    public void EnqueueUpload(IEnumerable<LocalFsItem> locals, string remoteDir)
    {
        foreach (var local in locals)
            AddJob(new Job(
                new TransferItem { Name = local.Name, Direction = "上传" },
                local.FullName,
                FtpManagerViewModel.JoinRemote(remoteDir, local.Name),
                Upload: true,
                local.IsDirectory,
                local.Size));
        Pump();
    }

    public void EnqueueDownload(IEnumerable<FtpEntry> items, string localDir)
    {
        foreach (var remote in items)
            AddJob(new Job(
                new TransferItem { Name = remote.Name, Direction = "下载" },
                Path.Combine(localDir, remote.Name),
                remote.FullName,
                Upload: false,
                remote.IsDirectory,
                remote.Size));
        Pump();
    }

    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    public void CancelAll()
    {
        try { _cts?.Cancel(); } catch { }
        while (_pending.Count > 0)
        {
            var job = _pending.Dequeue();
            job.Item.Status = "已取消";
        }
    }

    public void RetryFailed()
    {
        if (_failed.Count == 0)
            return;
        foreach (var job in _failed)
        {
            job.Item.Status = "排队中";
            job.Item.Progress = 0;
            _pending.Enqueue(job);
        }
        _failed.Clear();
        Pump();
    }

    private void AddJob(Job job)
    {
        Items.Insert(0, job.Item);
        _pending.Enqueue(job);
    }

    private void Pump() => _ = PumpAsync();

    private async Task PumpAsync()
    {
        if (_running)
            return;
        _running = true;
        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var workerCount = Math.Clamp(_site.MaxConcurrentTransfers, 1, 8);
            var workers = Enumerable.Range(0, workerCount)
                .Select(_ => WorkerAsync(cts.Token))
                .ToArray();
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            var restart = _pending.Count > 0 && !cts.IsCancellationRequested;
            _running = false;
            _cts = null;
            cts.Dispose();
            _afterTransfer();
            if (restart)
                Pump();
        }
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_paused)
            {
                await Task.Delay(150, ct);
                continue;
            }

            Job? job = null;
            lock (_pending)
            {
                if (_pending.Count > 0)
                    job = _pending.Dequeue();
            }

            if (job is null)
            {
                await Task.Delay(50, ct);
                lock (_pending)
                {
                    if (_pending.Count == 0)
                        return;
                }
                continue;
            }

            await ProcessAsync(job, ct);
        }
    }

    private async Task ProcessAsync(Job job, CancellationToken ct)
    {
        FtpSession? session = null;
        job.Item.Status = "进行中";
        job.Item.StartedText = DateTime.Now.ToString("HH:mm:ss");
        var started = DateTime.UtcNow;
        try
        {
            session = await _sessionFactory(ct);
            if (job.Upload)
                await UploadAsync(session, job, ct);
            else
                await DownloadAsync(session, job, ct);
            job.Item.Progress = 100;
            job.Item.Status = "完成";
            job.Item.DurationText = FormatDuration(DateTime.UtcNow - started);
            await AuditAsync(job, true, null);
        }
        catch (OperationCanceledException)
        {
            job.Item.Status = "已取消";
        }
        catch (Exception ex)
        {
            job.Item.Status = "失败：" + ex.Message;
            _failed.Add(job);
            await AuditAsync(job, false, ex.Message);
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync();
        }
    }

    private async Task UploadAsync(FtpSession session, Job job, CancellationToken ct)
    {
        if (!job.IsDirectory)
        {
            var target = await ResolveUploadTargetAsync(session, job.RemotePath, ct);
            if (target is null)
            {
                job.Item.Status = "已跳过";
                return;
            }

            var progress = new Progress<double>(value => job.Item.Progress = value);
            await session.UploadAsync(job.LocalPath, target.Value.Path, progress, target.Value.Mode, ct);
            return;
        }

        job.Item.IsIndeterminate = true;
        await session.CreateDirectoryAsync(job.RemotePath, ct);
        var files = await Task.Run(
            () => Directory.EnumerateFiles(job.LocalPath, "*", SearchOption.AllDirectories).ToArray(), ct);
        for (var i = 0; i < files.Length; i++)
        {
            var file = files[i];
            var relative = Path.GetRelativePath(job.LocalPath, file).Replace('\\', '/');
            var remotePath = FtpManagerViewModel.JoinRemote(job.RemotePath, relative);
            var target = await ResolveUploadTargetAsync(session, remotePath, ct);
            if (target is not null)
                await session.UploadAsync(file, target.Value.Path, null, target.Value.Mode, ct);
            job.Item.Progress = files.Length == 0 ? 100 : (i + 1) * 100.0 / files.Length;
        }
        job.Item.IsIndeterminate = false;
    }

    private async Task DownloadAsync(FtpSession session, Job job, CancellationToken ct)
    {
        if (!job.IsDirectory)
        {
            var target = ResolveDownloadTarget(job.LocalPath);
            if (target is null)
            {
                job.Item.Status = "已跳过";
                return;
            }

            var progress = new Progress<double>(value => job.Item.Progress = value);
            await session.DownloadAsync(job.RemotePath, target.Value.Path, progress, target.Value.Mode, ct);
            return;
        }

        job.Item.IsIndeterminate = true;
        await DownloadDirectoryAsync(session, job.RemotePath, job.LocalPath, job.Item, ct);
        job.Item.IsIndeterminate = false;
    }

    private async Task DownloadDirectoryAsync(
        FtpSession session, string remotePath, string localPath, TransferItem item, CancellationToken ct)
    {
        Directory.CreateDirectory(localPath);
        var entries = await session.ListAsync(remotePath, ct);
        foreach (var entry in entries)
        {
            var target = Path.Combine(localPath, entry.Name);
            if (entry.IsDirectory)
            {
                await DownloadDirectoryAsync(session, entry.FullName, target, item, ct);
                continue;
            }

            var resolved = ResolveDownloadTarget(target);
            if (resolved is not null)
                await session.DownloadAsync(entry.FullName, resolved.Value.Path, null, resolved.Value.Mode, ct);
        }
    }

    private async Task<(string Path, FtpRemoteExists Mode)?> ResolveUploadTargetAsync(
        FtpSession session, string remotePath, CancellationToken ct)
    {
        if (!await session.FileExistsAsync(remotePath, ct))
            return (remotePath, FtpRemoteExists.Overwrite);

        return _site.ConflictPolicy switch
        {
            FileTransferConflictPolicy.Overwrite => (remotePath, FtpRemoteExists.Overwrite),
            FileTransferConflictPolicy.Skip => null,
            FileTransferConflictPolicy.Resume => (remotePath, FtpRemoteExists.Resume),
            FileTransferConflictPolicy.Rename => (await UniqueRemotePathAsync(session, remotePath, ct),
                FtpRemoteExists.Overwrite),
            _ => AskUpload(remotePath)
        };
    }

    private (string Path, FtpRemoteExists Mode)? AskUpload(string remotePath)
    {
        var answer = _dialogs.AskYesNoCancel(
            $"远程文件已存在：\n{remotePath}\n\n是：覆盖\n否：跳过\n取消：停止当前传输",
            "上传冲突");
        return answer switch
        {
            UserDialogAnswer.Yes => (remotePath, FtpRemoteExists.Overwrite),
            UserDialogAnswer.Cancel => throw new OperationCanceledException("用户取消上传"),
            _ => null
        };
    }

    private (string Path, FtpLocalExists Mode)? ResolveDownloadTarget(string localPath)
    {
        if (!File.Exists(localPath))
            return (localPath, FtpLocalExists.Overwrite);

        return _site.ConflictPolicy switch
        {
            FileTransferConflictPolicy.Overwrite => (localPath, FtpLocalExists.Overwrite),
            FileTransferConflictPolicy.Skip => null,
            FileTransferConflictPolicy.Resume => (localPath, FtpLocalExists.Resume),
            FileTransferConflictPolicy.Rename => (UniqueLocalPath(localPath), FtpLocalExists.Overwrite),
            _ => AskDownload(localPath)
        };
    }

    private (string Path, FtpLocalExists Mode)? AskDownload(string localPath)
    {
        var answer = _dialogs.AskYesNoCancel(
            $"本地文件已存在：\n{localPath}\n\n是：覆盖\n否：跳过\n取消：停止当前传输",
            "下载冲突");
        return answer switch
        {
            UserDialogAnswer.Yes => (localPath, FtpLocalExists.Overwrite),
            UserDialogAnswer.Cancel => throw new OperationCanceledException("用户取消下载"),
            _ => null
        };
    }

    private static async Task<string> UniqueRemotePathAsync(
        FtpSession session, string remotePath, CancellationToken ct)
    {
        var candidate = remotePath;
        for (var i = 1; i < 10000; i++)
        {
            candidate = UniqueRemoteCandidate(remotePath, i);
            if (!await session.FileExistsAsync(candidate, ct))
                return candidate;
        }
        throw new IOException("无法生成不重复的远程文件名。");
    }

    private static string UniqueRemoteCandidate(string path, int index)
    {
        var slash = path.LastIndexOf('/');
        var directory = slash < 0 ? string.Empty : path[..slash];
        var name = slash < 0 ? path : path[(slash + 1)..];
        return $"{directory}/{Path.GetFileNameWithoutExtension(name)} ({index}){Path.GetExtension(name)}";
    }

    private static string UniqueLocalPath(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; i < 10000; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
        throw new IOException("无法生成不重复的本地文件名。");
    }

    private async Task AuditAsync(Job job, bool success, string? message)
    {
        try
        {
            await _audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = _site.Id,
                ServerName = _site.Name,
                Command = job.Upload ? "file_transfer: upload" : "file_transfer: download",
                Result = message,
                Status = success ? CommandStatus.Executed : CommandStatus.Failed,
                Category = AuditCategory.Transfer,
                Tool = "file_transfer",
                FilePath = job.Upload ? job.RemotePath : job.LocalPath,
                FileSize = job.Size > 0 ? job.Size : null,
                IsFileTransfer = true,
                Timestamp = DateTime.UtcNow
            });
        }
        catch
        {
        }
    }

    private static string FormatDuration(TimeSpan value)
        => value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");

    private sealed record Job(
        TransferItem Item,
        string LocalPath,
        string RemotePath,
        bool Upload,
        bool IsDirectory,
        long Size);
}
