using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.FileTransfer;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public sealed class FtpManagerViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly FtpFileService _service;
    private readonly IAuditLogService _audit;
    private readonly IUserDialogService _dialogs;
    private FtpSession? _session;
    private string _currentPath;
    private string _statusMessage = "尚未连接";
    private bool _isBusy;
    private bool _isConnected;
    private FtpEntry? _selectedItem;
    private CancellationTokenSource? _connectCts;

    public FtpManagerViewModel(
        FileTransferSiteConfig site,
        FtpFileService service,
        IAuditLogService audit,
        IUserDialogService dialogs)
    {
        Site = site;
        _service = service;
        _audit = audit;
        _dialogs = dialogs;
        _currentPath = string.IsNullOrWhiteSpace(site.InitialPath) ? "/" : site.InitialPath;
        Local = new LocalFileBrowserViewModel();
        Queue = new FtpTransferQueueViewModel(
            ct => _service.ConnectAsync(Site, ct),
            Site,
            RefreshAfterTransfer,
            _audit,
            _dialogs);
    }

    public FileTransferSiteConfig Site { get; }
    public LocalFileBrowserViewModel Local { get; }
    public FtpTransferQueueViewModel Queue { get; }
    public ObservableCollection<FtpEntry> RemoteItems { get; } = new();
    public string Title => $"文件传输 - {Site.Name}";
    public string Subtitle => $"{Site.ProtocolText} · {Site.Host}:{Site.Port}";
    public bool IsInsecure => Site.IsInsecure;

    public FtpEntry? SelectedItem
    {
        get => _selectedItem;
        set { _selectedItem = value; OnPropertyChanged(); }
    }

    private IReadOnlyList<FtpEntry> _selectedItems = Array.Empty<FtpEntry>();
    public IReadOnlyList<FtpEntry> SelectedItems
    {
        get => _selectedItems;
        set { _selectedItems = value; OnPropertyChanged(); }
    }

    public string CurrentPath
    {
        get => _currentPath;
        set { _currentPath = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; OnPropertyChanged(); }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set { _isConnected = value; OnPropertyChanged(); }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (IsConnected)
            return;
        await ConnectAsync(ct);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected || IsBusy)
            return;
        IsBusy = true;
        _connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            StatusMessage = "正在连接…";
            if (_session is not null)
                await _session.DisposeAsync();
            _session = await _service.ConnectAsync(Site, _connectCts.Token);
            IsConnected = true;
            Site.LastConnectedAt = DateTime.UtcNow;
            await LoadRemoteAsync(Site.InitialPath, _connectCts.Token);
            StatusMessage = $"已连接 {Site.Host}:{Site.Port}";
            await AuditAsync("file_transfer: connect", Site.Host, true, StatusMessage);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "连接已取消";
        }
        catch (Exception ex)
        {
            IsConnected = false;
            StatusMessage = "连接失败：" + ex.Message;
            await AuditAsync("file_transfer: connect", Site.Host, false, ex.Message);
        }
        finally
        {
            IsBusy = false;
            _connectCts?.Dispose();
            _connectCts = null;
        }
    }

    public async Task DisconnectAsync()
    {
        _connectCts?.Cancel();
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
        IsConnected = false;
        RemoteItems.Clear();
        StatusMessage = "已断开";
    }

    public async Task LoadRemoteAsync(string? path = null, CancellationToken ct = default)
    {
        if (_session is null)
            return;
        IsBusy = true;
        try
        {
            if (!_session.IsConnected && Site.AutoReconnect)
            {
                await _session.DisposeAsync();
                _session = await _service.ConnectAsync(Site, ct);
                IsConnected = true;
            }
            if (!_session.IsConnected)
            {
                IsConnected = false;
                StatusMessage = "连接已断开";
                return;
            }

            var target = string.IsNullOrWhiteSpace(path) ? CurrentPath : path;
            StatusMessage = "正在读取目录…";
            var items = await _session.ListAsync(target, ct);
            RemoteItems.Clear();
            foreach (var item in items)
                RemoteItems.Add(item);
            CurrentPath = _session.CurrentPath;
            StatusMessage = $"{RemoteItems.Count} 项";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "读取已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = "读取目录失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task GoUpAsync()
    {
        if (CurrentPath is "/" or "\\" or "")
            return;
        var trimmed = CurrentPath.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        await LoadRemoteAsync(index <= 0 ? "/" : trimmed[..index]);
    }

    public async Task OpenAsync(FtpEntry item)
    {
        if (item.IsDirectory)
        {
            await LoadRemoteAsync(item.FullName);
            return;
        }

        Queue.EnqueueDownload(new[] { item }, Local.CurrentPath);
    }

    public void UploadSelected()
    {
        var items = Local.SelectedItems.Count > 0
            ? Local.SelectedItems
            : Local.SelectedItem is { } item ? new[] { item } : Array.Empty<LocalFsItem>();
        if (items.Count > 0)
            Queue.EnqueueUpload(items, CurrentPath);
    }

    public void DownloadSelected()
    {
        var items = SelectedItems.Count > 0
            ? SelectedItems
            : SelectedItem is { } item ? new[] { item } : Array.Empty<FtpEntry>();
        if (items.Count > 0)
            Queue.EnqueueDownload(items, Local.CurrentPath);
    }

    public async Task NewFolderAsync(string name)
    {
        if (_session is null || string.IsNullOrWhiteSpace(name))
            return;
        if (name.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            StatusMessage = "目录名不能包含路径分隔符";
            return;
        }
        IsBusy = true;
        try
        {
            var path = FtpSession.NormalizeRemotePath(JoinRemote(CurrentPath, name.Trim()));
            await _session.CreateDirectoryAsync(path);
            StatusMessage = "已创建目录：" + path;
            await AuditAsync("file_transfer: mkdir", path, true, StatusMessage);
            await LoadRemoteAsync(CurrentPath);
        }
        catch (Exception ex)
        {
            StatusMessage = "新建目录失败：" + ex.Message;
            await AuditAsync("file_transfer: mkdir", name, false, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RenameSelectedAsync(string newName)
    {
        if (_session is null || SelectedItem is not { } item || string.IsNullOrWhiteSpace(newName))
            return;
        if (newName.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            StatusMessage = "名称不能包含路径分隔符";
            return;
        }
        IsBusy = true;
        try
        {
            var target = FtpSession.NormalizeRemotePath(JoinRemote(CurrentPath, newName.Trim()));
            await _session.RenameAsync(item.FullName, target);
            StatusMessage = $"已重命名 {item.Name} → {newName.Trim()}";
            await AuditAsync("file_transfer: rename", item.FullName, true, StatusMessage);
            await LoadRemoteAsync(CurrentPath);
        }
        catch (Exception ex)
        {
            StatusMessage = "重命名失败：" + ex.Message;
            await AuditAsync("file_transfer: rename", item.FullName, false, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteSelectedAsync()
    {
        if (_session is null || SelectedItem is not { } item)
            return;
        if (!_dialogs.Confirm(
                $"永久删除远程{(item.IsDirectory ? "目录" : "文件")}「{item.Name}」？\n\nFTP 没有远程回收站，删除后无法恢复。",
                "删除文件"))
            return;

        IsBusy = true;
        try
        {
            await _session.DeleteAsync(item.FullName, item.IsDirectory);
            StatusMessage = "已删除 " + item.Name;
            await AuditAsync("file_transfer: delete", item.FullName, true, StatusMessage);
            await LoadRemoteAsync(CurrentPath);
        }
        catch (Exception ex)
        {
            StatusMessage = "删除失败：" + ex.Message;
            await AuditAsync("file_transfer: delete", item.FullName, false, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshAfterTransfer()
    {
        Local.Load();
        _ = LoadRemoteAsync(CurrentPath);
    }

    public async Task AuditAsync(string command, string? path, bool success, string? message)
    {
        try
        {
            await _audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = Site.Id,
                ServerName = Site.Name,
                Command = command,
                Result = message,
                Status = success ? CommandStatus.Executed : CommandStatus.Failed,
                Category = AuditCategory.Transfer,
                Tool = "file_transfer",
                FilePath = path,
                IsFileTransfer = true,
                Timestamp = DateTime.UtcNow
            });
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Queue.CancelAll();
        await DisconnectAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    internal static string JoinRemote(string directory, string name)
        => FtpSession.NormalizeRemotePath(directory.TrimEnd('/') + "/" + name.TrimStart('/'));
}
