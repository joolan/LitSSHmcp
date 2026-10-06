using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.App.ViewModels;

/// <summary>
/// 远程文件浏览器（SFTP）：目录导航、上传/下载、新建/重命名/删除。
/// 与交互式终端共用一个服务器的连接信息，但文件操作走独立的 SFTP 连接。
/// </summary>
public sealed class RemoteFileBrowserViewModel : INotifyPropertyChanged
{
    private readonly ISshService _ssh;
    private readonly SshServerConfig _server;

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
                    LastModified = file.LastModified
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

    public Task GoUpAsync()
    {
        var parent = ParentOf(CurrentPath);
        return LoadAsync(parent);
    }

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

    public async Task UploadAsync(string localPath)
    {
        var name = System.IO.Path.GetFileName(localPath);
        var remote = JoinPath(CurrentPath, name);
        IsBusy = true;
        StatusMessage = $"上传中: {name}…";
        try
        {
            var result = await _ssh.UploadFileAsync(_server, localPath, remote);
            StatusMessage = result.Success ? $"已上传: {name}" : "上传失败: " + result.Message;
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

    /// <summary>下载到临时目录并用系统默认程序打开（简单编辑）。</summary>
    public async Task<string?> OpenFileAsync(RemoteFileItem item)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "litssh-remote");
        System.IO.Directory.CreateDirectory(dir);
        var local = System.IO.Path.Combine(dir, item.Name);
        await DownloadAsync(item, local);
        return System.IO.File.Exists(local) ? local : null;
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
