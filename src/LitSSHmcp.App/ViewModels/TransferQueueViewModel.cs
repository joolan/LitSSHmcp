using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.App.ViewModels;

/// <summary>传输队列中的一项。</summary>
public sealed class TransferItem : INotifyPropertyChanged
{
    public required string Name { get; init; }
    public required string Direction { get; init; }

    private string _status = "排队中";
    public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }

    private double _progress;
    public double Progress { get => _progress; set { _progress = value; OnPropertyChanged(); } }

    private bool _isIndeterminate;
    public bool IsIndeterminate { get => _isIndeterminate; set { _isIndeterminate = value; OnPropertyChanged(); } }

    private string _startedText = string.Empty;
    public string StartedText { get => _startedText; set { _startedText = value; OnPropertyChanged(); } }

    private string _durationText = string.Empty;
    public string DurationText { get => _durationText; set { _durationText = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>上传/下载传输队列：串行执行并汇报进度。</summary>
public sealed class TransferQueueViewModel : INotifyPropertyChanged
{
    private readonly ISshService _ssh;
    private readonly SshServerConfig _server;
    private readonly Func<Task>? _afterUpload;
    private readonly Func<Task>? _afterDownload;
    private readonly Queue<Job> _pending = new();
    private bool _running;
    private bool _overwriteAll;
    private bool _skipAll;
    private bool _cancelAll;

    public TransferQueueViewModel(ISshService ssh, SshServerConfig server, Func<Task>? afterUpload = null, Func<Task>? afterDownload = null)
    {
        _ssh = ssh;
        _server = server;
        _afterUpload = afterUpload;
        _afterDownload = afterDownload;
    }

    public ObservableCollection<TransferItem> Items { get; } = new();

    public void EnqueueUpload(IEnumerable<LocalFsItem> locals, string remoteDir)
    {
        foreach (var local in locals)
        {
            var item = new TransferItem { Name = local.Name, Direction = "上传" };
            Items.Insert(0, item);
            _pending.Enqueue(new Job(item, local.FullName, JoinRemote(remoteDir, local.Name), true, local.IsDirectory));
        }
        _ = PumpAsync();
    }

    public void EnqueueDownload(IEnumerable<RemoteFileItem> remotes, string localDir)
    {
        foreach (var remote in remotes)
        {
            var item = new TransferItem { Name = remote.Name, Direction = "下载" };
            Items.Insert(0, item);
            _pending.Enqueue(new Job(item, Path.Combine(localDir, remote.Name), remote.FullName, false, remote.IsDirectory));
        }
        _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        if (_running)
            return;
        _running = true;
        try
        {
            while (_pending.Count > 0)
                await RunAsync(_pending.Dequeue());
        }
        finally
        {
            _running = false;
        }
    }

    private async Task RunAsync(Job job)
    {
        if (_cancelAll)
        {
            job.Item.Status = "已取消";
            return;
        }

        job.Item.Status = "进行中…";
        job.Item.IsIndeterminate = job.IsDirectory;
        job.Item.StartedText = DateTime.Now.ToString("HH:mm:ss");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (job.IsUpload)
            {
                if (job.IsDirectory)
                {
                    var result = await _ssh.UploadBatchAsync(_server, new[] { job.LocalPath }, GetRemoteDir(job.RemotePath),
                        recursive: true, maxFiles: 5000, maxFileBytes: 512L * 1024 * 1024, maxTotalBytes: 2L * 1024 * 1024 * 1024,
                        overwriteResolver: ResolveConflict);
                    Finish(job, result.Success, result.Error, result.Succeeded, result.Failed);
                }
                else
                {
                    if (await _ssh.RemoteFileExistsAsync(_server, job.RemotePath) && !ResolveConflict(job.RemotePath))
                    {
                        job.Item.Status = "已跳过(同名)";
                    }
                    else
                    {
                        var progress = MakeProgress(job.Item);
                        var result = await _ssh.UploadFileAsync(_server, job.LocalPath, job.RemotePath, progress);
                        Finish(job, result.Success, result.Message);
                    }
                }
            }
            else
            {
                if (job.IsDirectory)
                {
                    var result = await _ssh.DownloadBatchAsync(_server, new[] { job.RemotePath }, Path.GetDirectoryName(job.LocalPath) ?? ".",
                        recursive: true, maxFiles: 5000, maxTotalBytes: 2L * 1024 * 1024 * 1024,
                        overwriteResolver: ResolveConflict);
                    Finish(job, result.Success, result.Error, result.Succeeded, result.Failed);
                }
                else
                {
                    if (File.Exists(job.LocalPath) && !ResolveConflict(job.LocalPath))
                    {
                        job.Item.Status = "已跳过(同名)";
                    }
                    else
                    {
                        var progress = MakeProgress(job.Item);
                        var result = await _ssh.DownloadFileAsync(_server, job.RemotePath, job.LocalPath, progress);
                        Finish(job, result.Success, result.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            job.Item.Status = "失败: " + ex.Message;
        }

        sw.Stop();
        job.Item.DurationText = $"{sw.Elapsed.TotalSeconds:0.0}s";
        job.Item.IsIndeterminate = false;
        job.Item.Progress = 100;
        if (job.IsUpload)
        {
            if (_afterUpload is not null)
                await _afterUpload();
        }
        else if (_afterDownload is not null)
        {
            await _afterDownload();
        }
    }

    /// <summary>同名冲突处理：返回 true=覆盖，false=跳过；支持「全部覆盖/全部跳过/取消」。</summary>
    private bool ResolveConflict(string targetPath)
    {
        if (_overwriteAll)
            return true;
        if (_skipAll || _cancelAll)
            return false;

        var dispatcher = Application.Current?.Dispatcher;
        var choice = dispatcher is not null
            ? dispatcher.Invoke(() => Views.ConflictDialog.Ask(ActiveWindow(), targetPath))
            : Views.ConflictChoice.Overwrite;

        return choice switch
        {
            Views.ConflictChoice.OverwriteAll => _overwriteAll = true,
            Views.ConflictChoice.SkipAll => _skipAll = true,
            Views.ConflictChoice.Skip => false,
            Views.ConflictChoice.Overwrite => true,
            _ => _cancelAll = true
        };
    }

    /// <summary>取当前激活窗口作为弹窗 Owner（避免弹到主界面）。</summary>
    private static Window? ActiveWindow()
    {
        var windows = Application.Current?.Windows;
        if (windows is null)
            return null;
        foreach (Window window in windows)
        {
            if (window.IsActive)
                return window;
        }
        return Application.Current?.MainWindow;
    }

    private void Finish(Job job, bool success, string? error, int? succeeded = null, int? failed = null)
    {
        if (success)
        {
            job.Item.Status = failed is > 0 ? $"完成（成功 {succeeded}，跳过/失败 {failed}）" : "完成";
        }
        else
        {
            job.Item.Status = "失败: " + (error ?? "未知错误");
        }
    }

    private IProgress<FileTransferProgress> MakeProgress(TransferItem item)
        => new Progress<FileTransferProgress>(p => item.Progress = p.Percentage);

    private static string GetRemoteDir(string remotePath)
    {
        var idx = remotePath.TrimEnd('/').LastIndexOf('/');
        return idx <= 0 ? "/" : remotePath[..idx];
    }

    private static string JoinRemote(string dir, string name)
        => string.IsNullOrEmpty(dir) || dir == "." ? name : dir.TrimEnd('/') + "/" + name;

    private sealed record Job(TransferItem Item, string LocalPath, string RemotePath, bool IsUpload, bool IsDirectory);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
