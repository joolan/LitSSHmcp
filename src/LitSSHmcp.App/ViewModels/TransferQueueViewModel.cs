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

    private bool _canCancel;
    /// <summary>排队中/进行中的项才显示“取消”按钮。</summary>
    public bool CanCancel { get => _canCancel; set { if (_canCancel == value) return; _canCancel = value; OnPropertyChanged(); } }

    internal void SetStatus(string status, bool canCancel)
    {
        Status = status;
        CanCancel = canCancel;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>上传/下载传输队列：串行执行并汇报进度，支持暂停队列与取消单项/全部。</summary>
public sealed class TransferQueueViewModel : INotifyPropertyChanged
{
    private readonly ISshService _ssh;
    private readonly SshServerConfig _server;
    private readonly Func<Task>? _afterUpload;
    private readonly Func<Task>? _afterDownload;
    private readonly List<Job> _pending = new();
    private bool _running;
    private bool _paused;
    private bool _overwriteAll;
    private bool _skipAll;
    private CancellationTokenSource? _queueCts;
    private Job? _current;
    private CancellationTokenSource? _currentCts;

    public TransferQueueViewModel(ISshService ssh, SshServerConfig server, Func<Task>? afterUpload = null, Func<Task>? afterDownload = null)
    {
        _ssh = ssh;
        _server = server;
        _afterUpload = afterUpload;
        _afterDownload = afterDownload;
    }

    public ObservableCollection<TransferItem> Items { get; } = new();

    public bool IsPaused => _paused;
    public string PauseButtonText => _paused ? "继续队列" : "暂停队列";
    public string QueueStateText => _paused ? "已暂停 · 当前传输完成后不再开始新任务" : string.Empty;

    public void EnqueueUpload(IEnumerable<LocalFsItem> locals, string remoteDir)
    {
        foreach (var local in locals)
        {
            var item = new TransferItem { Name = local.Name, Direction = "上传" };
            AddJob(new Job(item, local.FullName, JoinRemote(remoteDir, local.Name), true, local.IsDirectory));
        }
        Pump();
    }

    public void EnqueueDownload(IEnumerable<RemoteFileItem> remotes, string localDir)
    {
        foreach (var remote in remotes)
        {
            var item = new TransferItem { Name = remote.Name, Direction = "下载" };
            AddJob(new Job(item, Path.Combine(localDir, remote.Name), remote.FullName, false, remote.IsDirectory));
        }
        Pump();
    }

    private void AddJob(Job job)
    {
        job.Item.CanCancel = true;
        Items.Insert(0, job.Item);
        lock (_pending)
            _pending.Add(job);
    }

    /// <summary>暂停队列：当前正在传输的文件会完成，之后不再开始新任务。</summary>
    public void Pause()
    {
        if (_paused)
            return;
        _paused = true;
        RaiseQueueState();
    }

    public void Resume()
    {
        if (!_paused)
            return;
        _paused = false;
        RaiseQueueState();
        if (!_running && HasPending())
            Pump();
    }

    public void TogglePause()
    {
        if (_paused)
            Resume();
        else
            Pause();
    }

    /// <summary>取消当前传输与所有排队任务。</summary>
    public void CancelAll()
    {
        try { _queueCts?.Cancel(); } catch { /* ignore */ }
        try { _currentCts?.Cancel(); } catch { /* ignore */ }

        List<Job> canceled;
        lock (_pending)
        {
            canceled = _pending.ToList();
            _pending.Clear();
        }
        foreach (var job in canceled)
            job.Item.SetStatus("已取消", false);

        if (_current is not null)
        {
            _current.Item.Status = "取消中…";
            _current.Item.CanCancel = false;
        }
    }

    /// <summary>取消单个任务：排队中的直接移除；正在传输的请求取消并尽快停止。</summary>
    public void Cancel(TransferItem item)
    {
        if (item is null)
            return;

        lock (_pending)
        {
            var index = _pending.FindIndex(job => ReferenceEquals(job.Item, item));
            if (index >= 0)
            {
                _pending.RemoveAt(index);
                item.SetStatus("已取消", false);
                return;
            }
        }

        if (ReferenceEquals(_current?.Item, item))
        {
            item.Status = "取消中…";
            item.CanCancel = false;
            try { _currentCts?.Cancel(); } catch { /* ignore */ }
        }
    }

    private bool HasPending()
    {
        lock (_pending)
            return _pending.Count > 0;
    }

    private void Pump() => _ = PumpAsync();

    private async Task PumpAsync()
    {
        if (_running)
            return;
        _running = true;
        var cts = new CancellationTokenSource();
        _queueCts = cts;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (_paused)
                {
                    if (!HasPending())
                        break;
                    await Task.Delay(150, cts.Token);
                    continue;
                }

                Job? job;
                lock (_pending)
                {
                    job = _pending.Count > 0 ? _pending[0] : null;
                    if (job is not null)
                        _pending.RemoveAt(0);
                }
                if (job is null)
                    break;

                await RunAsync(job, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消全部时退出泵循环。
        }
        finally
        {
            var cancelled = cts.IsCancellationRequested;
            _running = false;
            _queueCts = null;
            cts.Dispose();
            if (!cancelled && HasPending())
                Pump();
        }
    }

    private async Task RunAsync(Job job, CancellationToken queueToken)
    {
        if (queueToken.IsCancellationRequested)
        {
            job.Item.SetStatus("已取消", false);
            return;
        }

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(queueToken);
        _current = job;
        _currentCts = jobCts;
        var ct = jobCts.Token;

        job.Item.SetStatus("进行中…", true);
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
                        overwriteResolver: path => ResolveConflict(path, ct), ct: ct);
                    Finish(job, result.Success, result.Error, result.Succeeded, result.Failed);
                }
                else
                {
                    if (await _ssh.RemoteFileExistsAsync(_server, job.RemotePath, ct) && !ResolveConflict(job.RemotePath, ct))
                    {
                        job.Item.SetStatus("已跳过(同名)", false);
                    }
                    else
                    {
                        var progress = MakeProgress(job.Item);
                        var result = await _ssh.UploadFileAsync(_server, job.LocalPath, job.RemotePath, progress, ct);
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
                        overwriteResolver: path => ResolveConflict(path, ct), ct: ct);
                    Finish(job, result.Success, result.Error, result.Succeeded, result.Failed);
                }
                else
                {
                    if (File.Exists(job.LocalPath) && !ResolveConflict(job.LocalPath, ct))
                    {
                        job.Item.SetStatus("已跳过(同名)", false);
                    }
                    else
                    {
                        var progress = MakeProgress(job.Item);
                        var result = await _ssh.DownloadFileAsync(_server, job.RemotePath, job.LocalPath, progress, ct);
                        Finish(job, result.Success, result.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            job.Item.SetStatus("已取消", false);
        }
        catch (Exception ex)
        {
            // 单文件传输的底层实现不一定立即响应取消；若用户已取消，优先显示“已取消”。
            job.Item.SetStatus(ct.IsCancellationRequested ? "已取消" : "失败: " + ex.Message, false);
        }
        finally
        {
            _current = null;
            _currentCts = null;
        }

        if (ct.IsCancellationRequested && !job.Item.Status.StartsWith("已取消", StringComparison.Ordinal))
            job.Item.SetStatus("已取消", false);

        sw.Stop();
        job.Item.DurationText = $"{sw.Elapsed.TotalSeconds:0.0}s";
        job.Item.IsIndeterminate = false;
        if (job.Item.Status.StartsWith("完成", StringComparison.Ordinal))
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
    private bool ResolveConflict(string targetPath, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return false;
        if (_overwriteAll)
            return true;
        if (_skipAll)
            return false;

        var dispatcher = Application.Current?.Dispatcher;
        var choice = dispatcher is not null
            ? dispatcher.Invoke(() => Views.ConflictDialog.Ask(ActiveWindow(), targetPath))
            : Views.ConflictChoice.Overwrite;

        switch (choice)
        {
            case Views.ConflictChoice.OverwriteAll:
                _overwriteAll = true;
                return true;
            case Views.ConflictChoice.SkipAll:
                _skipAll = true;
                return false;
            case Views.ConflictChoice.Skip:
                return false;
            case Views.ConflictChoice.Overwrite:
                return true;
            default:
                // 取消：停止当前传输并清空后续队列（与旧行为一致）。
                CancelAll();
                throw new OperationCanceledException();
        }
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

    private static void Finish(Job job, bool success, string? error, int? succeeded = null, int? failed = null)
    {
        if (success)
        {
            job.Item.SetStatus(failed is > 0 ? $"完成（成功 {succeeded}，跳过/失败 {failed}）" : "完成", false);
        }
        else
        {
            job.Item.SetStatus("失败: " + (error ?? "未知错误"), false);
        }
    }

    private static IProgress<FileTransferProgress> MakeProgress(TransferItem item)
        => new Progress<FileTransferProgress>(p => item.Progress = p.Percentage);

    private static string GetRemoteDir(string remotePath)
    {
        var idx = remotePath.TrimEnd('/').LastIndexOf('/');
        return idx <= 0 ? "/" : remotePath[..idx];
    }

    private static string JoinRemote(string dir, string name)
        => string.IsNullOrEmpty(dir) || dir == "." ? name : dir.TrimEnd('/') + "/" + name;

    private void RaiseQueueState()
    {
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseButtonText));
        OnPropertyChanged(nameof(QueueStateText));
    }

    private sealed record Job(TransferItem Item, string LocalPath, string RemotePath, bool IsUpload, bool IsDirectory);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
