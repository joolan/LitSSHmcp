using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.App.ViewModels;

/// <summary>
/// 远程文件浏览器（SFTP）：目录导航、上传/下载（支持文件夹）、新建/重命名/删除、编辑并回传。
/// 与交互式终端共用一个服务器的连接信息，但文件操作走独立的 SFTP 连接。
/// </summary>
public sealed class RemoteFileBrowserViewModel : INotifyPropertyChanged
{
    private readonly ISshService _ssh;
    private readonly SshServerConfig _server;
    private bool _initialized;

    private FileSystemWatcher? _editWatch;
    private string? _editLocalPath;
    private string? _editRemotePath;
    private CancellationTokenSource? _editDebounce;

    public RemoteFileBrowserViewModel(SshServerConfig server, ISshService ssh)
    {
        _server = server;
        _ssh = ssh;
        CurrentPath = ".";
    }

    public ObservableCollection<RemoteFileItem> Items { get; } = new();

    private RemoteFileItem? _selectedItem;
    public RemoteFileItem? SelectedItem
    {
        get => _selectedItem;
        set { _selectedItem = value; OnPropertyChanged(); }
    }

    private string _currentPath = ".";
    public string CurrentPath
    {
        get => _currentPath;
        set { _currentPath = value; OnPropertyChanged(); }
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); }
    }

    /// <summary>首次打开：定位到用户 home（避免因权限打不开根目录）。</summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
            return;
        _initialized = true;
        await LoadAsync(await ResolveHomeAsync());
    }

    private async Task<string> ResolveHomeAsync()
    {
        try
        {
            var result = await _ssh.ExecuteCommandAsync(_server, "printf %s \"$HOME\"");
            if (result.Success)
            {
                var home = result.Output.Trim();
                if (!string.IsNullOrEmpty(home) && home.StartsWith('/'))
                    return home;
            }
        }
        catch
        {
            // 忽略，回退默认
        }
        return ".";
    }

    public async Task LoadAsync(string? path = null)
    {
        if (!string.IsNullOrWhiteSpace(path))
            CurrentPath = path!;

        IsBusy = true;
        StatusMessage = "读取中…";
        try
        {
            var result = await _ssh.ListRemoteFilesAsync(_server, CurrentPath);
            Items.Clear();
            if (!result.Success)
            {
                StatusMessage = "读取失败: " + result.Error;
                return;
            }

            foreach (var file in result.Files
                         .OrderByDescending(f => f.IsDirectory)
                         .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                Items.Add(new RemoteFileItem
                {
                    Name = file.Name,
                    FullName = file.FullName,
                    IsDirectory = file.IsDirectory,
                    IsSymbolicLink = file.IsSymbolicLink,
                    Size = file.Size,
                    LastModified = file.LastModified,
                    Permissions = file.Permissions,
                    Owner = file.Owner
                });
            }

            StatusMessage = result.Truncated ? $"{Items.Count} 项（已截断）" : $"{Items.Count} 项";
        }
        catch (Exception ex)
        {
            StatusMessage = "读取失败: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task GoUpAsync() => LoadAsync(ParentOf(CurrentPath));

    public async Task NewFolderAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        var target = JoinPath(CurrentPath, name);
        await RunCommandAsync($"mkdir -p -- {Quote(target)}", $"已新建文件夹: {name}");
        await LoadAsync();
    }

    public async Task RenameAsync(RemoteFileItem item, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return;
        var target = JoinPath(CurrentPath, newName);
        await RunCommandAsync($"mv -- {Quote(item.FullName)} {Quote(target)}", $"已重命名: {item.Name} → {newName}");
        await LoadAsync();
    }

    public async Task DeleteAsync(IReadOnlyList<RemoteFileItem> items)
    {
        if (items.Count == 0)
            return;
        var args = string.Join(' ', items.Select(i => Quote(i.FullName)));
        await RunCommandAsync($"rm -rf -- {args}", $"已删除 {items.Count} 项");
        await LoadAsync();
    }

    /// <summary>上传本地文件/文件夹（递归）到当前目录。</summary>
    public async Task UploadPathsAsync(IEnumerable<string> localPaths)
    {
        var paths = localPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (paths.Count == 0)
            return;

        IsBusy = true;
        StatusMessage = "上传中…";
        try
        {
            var result = await _ssh.UploadBatchAsync(_server, paths, CurrentPath,
                recursive: true, maxFiles: 5000, maxFileBytes: 512L * 1024 * 1024, maxTotalBytes: 2L * 1024 * 1024 * 1024);
            StatusMessage = result.Success
                ? $"已上传 {result.Succeeded} 项" + (result.Failed > 0 ? $"，失败 {result.Failed}" : string.Empty)
                : "上传失败: " + result.Error;
        }
        catch (Exception ex)
        {
            StatusMessage = "上传失败: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            await LoadAsync();
        }
    }

    public async Task DownloadAsync(RemoteFileItem item, string localPath)
    {
        IsBusy = true;
        StatusMessage = $"下载中: {item.Name}…";
        try
        {
            var result = await _ssh.DownloadFileAsync(_server, item.FullName, localPath);
            StatusMessage = result.Success ? $"已下载: {localPath}" : "下载失败: " + result.Message;
        }
        catch (Exception ex)
        {
            StatusMessage = "下载失败: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>下载到临时目录、用系统默认程序打开，并监视改动自动回传 SFTP。</summary>
    public async Task<string?> OpenFileForEditAsync(RemoteFileItem item)
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-remote");
        Directory.CreateDirectory(dir);
        var local = Path.Combine(dir, item.Name);
        await DownloadAsync(item, local);
        if (!File.Exists(local))
            return null;

        StartEditWatch(local, item.FullName);
        return local;
    }

    private void StartEditWatch(string localPath, string remotePath)
    {
        StopEditWatch();
        _editLocalPath = localPath;
        _editRemotePath = remotePath;
        try
        {
            var dir = Path.GetDirectoryName(localPath);
            var name = Path.GetFileName(localPath);
            if (string.IsNullOrEmpty(dir))
                return;
            _editWatch = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            _editWatch.Changed += OnEdited;
            _editWatch.Created += OnEdited;
            _editWatch.Renamed += OnRenamed;
            _editWatch.EnableRaisingEvents = true;
        }
        catch
        {
            // 监视失败不影响编辑（用户可手动「回传」）
        }
    }

    private void OnEdited(object sender, FileSystemEventArgs e) => ScheduleUploadBack();
    private void OnRenamed(object sender, RenamedEventArgs e) => ScheduleUploadBack();

    private void ScheduleUploadBack()
    {
        _editDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _editDebounce = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(1500, cts.Token); }
            catch (TaskCanceledException) { return; }
            if (cts.IsCancellationRequested)
                return;
            await UploadEditBackAsync();
        });
    }

    /// <summary>把当前编辑的临时文件回传到原远程路径。</summary>
    public async Task UploadEditBackAsync()
    {
        var local = _editLocalPath;
        var remote = _editRemotePath;
        if (local is null || remote is null || !File.Exists(local))
            return;

        try
        {
            var result = await _ssh.UploadFileAsync(_server, local, remote);
            SetStatus(result.Success ? $"已回传: {Path.GetFileName(remote)}" : "回传失败: " + result.Message);
        }
        catch (Exception ex)
        {
            SetStatus("回传失败: " + ex.Message);
        }
    }

    private void StopEditWatch()
    {
        try { if (_editWatch is not null) _editWatch.EnableRaisingEvents = false; } catch { /* ignore */ }
        try { _editWatch?.Dispose(); } catch { /* ignore */ }
        _editWatch = null;
    }

    private void SetStatus(string message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            _ = dispatcher.BeginInvoke(new Action(() => StatusMessage = message));
        else
            StatusMessage = message;
    }

    private async Task RunCommandAsync(string command, string successMessage)
    {
        IsBusy = true;
        StatusMessage = "执行中…";
        try
        {
            var result = await _ssh.ExecuteCommandAsync(_server, command, timeoutSeconds: 30);
            StatusMessage = result.Success ? successMessage : "操作失败: " + (string.IsNullOrEmpty(result.Error) ? result.Output : result.Error);
        }
        catch (Exception ex)
        {
            StatusMessage = "操作失败: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string JoinPath(string dir, string name)
    {
        if (string.IsNullOrEmpty(dir) || dir == ".")
            return name;
        return dir.EndsWith('/') ? dir + name : dir + "/" + name;
    }

    private static string ParentOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "." || path == "/")
            return "/";
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        if (idx < 0)
            return ".";
        if (idx == 0)
            return "/";
        return trimmed[..idx];
    }

    private static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
