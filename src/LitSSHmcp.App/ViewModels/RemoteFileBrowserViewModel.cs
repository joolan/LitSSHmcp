using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using LitSSHmcp.App.Services;
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

    public SshServerConfig Server => _server;

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

    private string PathKey => "ssh.files.lastdir." + _server.Id;

    /// <summary>首次打开：优先上次目录，其次用户 home（避免因权限打不开根目录）。</summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
            return;
        _initialized = true;

        var saved = UiPrefs.GetString(PathKey, string.Empty);
        await LoadAsync(string.IsNullOrWhiteSpace(saved) ? await ResolveHomeAsync() : saved);
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
            UiPrefs.SetString(PathKey, CurrentPath);
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
        if (IsBusy)
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

    /// <summary>批量下载所选（含文件夹，递归）到本地目录。</summary>
    public async Task<bool> DownloadToDirectoryAsync(IReadOnlyList<RemoteFileItem> items, string localDirectory)
    {
        if (items.Count == 0)
            return false;
        if (IsBusy)
            return false;

        IsBusy = true;
        StatusMessage = $"下载 {items.Count} 项…";
        try
        {
            var result = await _ssh.DownloadBatchAsync(_server, items.Select(i => i.FullName).ToList(), localDirectory,
                recursive: true, maxFiles: 5000, maxTotalBytes: 2L * 1024 * 1024 * 1024);
            StatusMessage = result.Success
                ? $"已下载 {result.Succeeded} 项" + (result.Failed > 0 ? $"，失败 {result.Failed}" : string.Empty)
                : "下载失败: " + result.Error;
            return result.Success && result.Succeeded > 0;
        }
        catch (Exception ex)
        {
            StatusMessage = "下载失败: " + ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- 复制 / 剪切 / 粘贴 ----

    private readonly List<string> _clipboard = new();
    private bool _clipboardCut;

    public bool HasClipboard => _clipboard.Count > 0;

    /// <summary>请求在终端中 cd 到某目录（由终端标签注入）。</summary>
    public Action<string>? CdRequested { get; set; }

    /// <summary>切换到该项所在目录（目录本身 / 文件的父目录）并在终端执行 cd。</summary>
    public void RequestCd(RemoteFileItem item)
    {
        var dir = item.IsDirectory ? item.FullName : ParentOf(item.FullName);
        CdRequested?.Invoke(dir);
        StatusMessage = $"已在终端切换目录: {dir}";
    }

    public void CopyItems(IEnumerable<RemoteFileItem> items)
    {
        _clipboard.Clear();
        _clipboard.AddRange(items.Select(i => i.FullName));
        _clipboardCut = false;
        StatusMessage = $"已复制 {_clipboard.Count} 项到剪贴板";
    }

    public void CutItems(IEnumerable<RemoteFileItem> items)
    {
        _clipboard.Clear();
        _clipboard.AddRange(items.Select(i => i.FullName));
        _clipboardCut = true;
        StatusMessage = $"已剪切 {_clipboard.Count} 项";
    }

    public async Task PasteAsync()
    {
        if (_clipboard.Count == 0)
        {
            StatusMessage = "剪贴板为空";
            return;
        }
        if (IsBusy)
            return;

        var jobs = new List<(string Source, string Target, bool Conflict)>();
        foreach (var source in _clipboard)
        {
            var name = BaseName(source);
            var parent = ParentOf(source);
            var sameDir = string.Equals(parent, CurrentPath, StringComparison.Ordinal);

            if (sameDir && !_clipboardCut)
                continue; // 复制到原目录会产生嵌套，跳过
            if (sameDir && _clipboardCut)
                continue; // 剪切到原目录无意义

            var target = JoinPath(CurrentPath, name);
            var conflict = Items.Any(i => string.Equals(i.Name, name, StringComparison.Ordinal));
            jobs.Add((source, target, conflict));
        }

        if (jobs.Count == 0)
        {
            StatusMessage = "无需粘贴（与原目录相同）";
            return;
        }

        var conflicts = jobs.Count(j => j.Conflict);
        var overwrite = false;
        if (conflicts > 0)
        {
            var answer = MessageBox.Show(
                $"目标目录已存在 {conflicts} 个同名文件/文件夹，是否覆盖？",
                "粘贴", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            overwrite = answer == MessageBoxResult.Yes;
        }

        var commands = new List<string>();
        foreach (var (source, target, conflict) in jobs)
        {
            if (conflict && !overwrite)
                continue;
            var prefix = conflict ? $"rm -rf -- {Quote(target)} && " : string.Empty;
            commands.Add(_clipboardCut
                ? $"{prefix}mv -- {Quote(source)} {Quote(target)}"
                : $"{prefix}cp -a -- {Quote(source)} {Quote(target)}");
        }

        if (commands.Count == 0)
        {
            StatusMessage = "已取消粘贴";
            return;
        }

        await RunCommandAsync(string.Join(" && ", commands), _clipboardCut ? "已剪切粘贴" : "已复制粘贴");
        if (_clipboardCut)
            _clipboard.Clear();
        await LoadAsync();
    }

    /// <summary>应用权限（chmod）与属主/属组（chown）；返回是否成功。</summary>
    public async Task<bool> ApplyPermissionsAsync(RemoteFileItem item, string mode, string? owner, string? group, bool recursive)
    {
        var commands = new List<string>();
        var flag = recursive ? "-R " : string.Empty;

        if (!string.IsNullOrWhiteSpace(mode))
        {
            if (!IsValidMode(mode))
            {
                StatusMessage = "权限格式应为 3~4 位八进制（如 755 / 644）";
                return false;
            }
            commands.Add($"chmod {flag}{Quote(mode.Trim())} {Quote(item.FullName)}");
        }

        if (!string.IsNullOrWhiteSpace(owner))
        {
            var ownerGroup = owner.Trim() + (string.IsNullOrWhiteSpace(group) ? string.Empty : ":" + group.Trim());
            commands.Add($"chown {flag}{Quote(ownerGroup)} {Quote(item.FullName)}");
        }

        if (commands.Count == 0)
        {
            StatusMessage = "没有需要修改的属性";
            return false;
        }

        IsBusy = true;
        try
        {
            var result = await _ssh.ExecuteCommandAsync(_server, string.Join(" && ", commands), timeoutSeconds: 30);
            if (result.Success)
            {
                StatusMessage = "属性已更新";
                await LoadAsync();
                return true;
            }

            StatusMessage = "修改失败: " + (string.IsNullOrEmpty(result.Error) ? result.Output : result.Error);
            return false;
        }
        catch (Exception ex)
        {
            StatusMessage = "修改失败: " + ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>读取服务器上的真实账号/用户组名（属主下拉用）。</summary>
    public async Task<(List<string> Users, List<string> Groups)> LoadAccountsAsync()
    {
        var users = new List<string>();
        var groups = new List<string>();
        try
        {
            users = await ReadNamesAsync("getent passwd | cut -d: -f1", "cut -d: -f1 /etc/passwd");
            groups = await ReadNamesAsync("getent group | cut -d: -f1", "cut -d: -f1 /etc/group");
        }
        catch
        {
            // 读取失败：返回空列表
        }
        return (users, groups);
    }

    private async Task<List<string>> ReadNamesAsync(string primary, string fallback)
    {
        var result = await _ssh.ExecuteCommandAsync(_server, primary, timeoutSeconds: 15);
        var names = ParseNames(result.Success ? result.Output : string.Empty);
        if (names.Count == 0)
        {
            var fallbackResult = await _ssh.ExecuteCommandAsync(_server, fallback, timeoutSeconds: 15);
            names = ParseNames(fallbackResult.Success ? fallbackResult.Output : string.Empty);
        }
        return names;
    }

    private static List<string> ParseNames(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    private static bool IsValidMode(string mode)
        => mode.Trim().Length is 3 or 4 && mode.Trim().All(c => c >= '0' && c <= '7');

    private static string BaseName(string path)
    {
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
    }

    /// <summary>检查服务器是否安装了某命令（command -v）。</summary>
    public async Task<bool> HasCommandAsync(string command)
    {
        try
        {
            var result = await _ssh.ExecuteCommandAsync(_server, $"command -v {command}");
            return result.Success && !string.IsNullOrWhiteSpace(result.Output);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>压缩所选文件/文件夹（.zip 用 zip，.tar.gz/.tgz 用 tar）。返回是否成功。</summary>
    public async Task<(bool Ok, string Message)> CompressAsync(IReadOnlyList<RemoteFileItem> items, string archiveName)
    {
        if (items.Count == 0)
            return (false, "请先选择要压缩的文件/文件夹");
        if (string.IsNullOrWhiteSpace(archiveName))
            return (false, "请输入压缩文件名");

        var name = archiveName.Trim();
        var isZip = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        var isTar = name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);
        if (!isZip && !isTar)
        {
            name += ".zip";
            isZip = true;
        }

        if (Items.Any(i => string.Equals(i.Name, name, StringComparison.Ordinal)))
            return (false, "当前目录已存在同名文件: " + name);

        var names = string.Join(' ', items.Select(i => Quote(i.Name)));
        string command;
        if (isZip)
        {
            if (!await HasCommandAsync("zip"))
                return (false, "服务器未安装 zip，无法压缩为 .zip（可改用 .tar.gz）");
            command = $"cd {Quote(CurrentPath)} && zip -r {Quote(name)} {names}";
        }
        else
        {
            if (!await HasCommandAsync("tar"))
                return (false, "服务器未安装 tar，无法压缩为 .tar.gz");
            command = $"cd {Quote(CurrentPath)} && tar -czf {Quote(name)} {names}";
        }

        await RunCommandAsync(command, $"已压缩为 {name}");
        await LoadAsync();
        return (true, $"已压缩为 {name}");
    }

    /// <summary>列出压缩包内的条目名（用于解压前检测同名冲突）；失败返回 null。</summary>
    public async Task<List<string>?> ListArchiveEntriesAsync(RemoteFileItem archive)
    {
        string command;
        if (IsZip(archive.Name))
            command = $"cd {Quote(CurrentPath)} && unzip -Z1 {Quote(archive.Name)}";
        else if (IsTarGz(archive.Name))
            command = $"cd {Quote(CurrentPath)} && tar -tzf {Quote(archive.Name)}";
        else
            return null;

        try
        {
            var result = await _ssh.ExecuteCommandAsync(_server, command, timeoutSeconds: 30);
            if (!result.Success)
                return null;
            return result.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => n.TrimStart('.', '/'))
                .Where(n => n.Length > 0)
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解压 .zip / .tar.gz 到当前目录；overwrite=false 时跳过同名项。</summary>
    public async Task<(bool Ok, string Message)> ExtractAsync(RemoteFileItem archive, bool overwrite)
    {
        string command;
        if (IsZip(archive.Name))
        {
            if (!await HasCommandAsync("unzip"))
                return (false, "服务器未安装 unzip，无法解压 .zip");
            command = $"cd {Quote(CurrentPath)} && unzip {(overwrite ? "-o" : "-n")} {Quote(archive.Name)}";
        }
        else if (IsTarGz(archive.Name))
        {
            if (!await HasCommandAsync("tar"))
                return (false, "服务器未安装 tar，无法解压 .tar.gz");
            var flag = overwrite ? string.Empty : "--skip-old-files ";
            command = $"cd {Quote(CurrentPath)} && tar -xzf {flag}{Quote(archive.Name)}";
        }
        else
        {
            return (false, "仅支持解压 .zip / .tar.gz / .tgz");
        }

        await RunCommandAsync(command, $"已解压 {archive.Name}");
        await LoadAsync();
        return (true, $"已解压 {archive.Name}");
    }

    public static bool IsZip(string name) => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    public static bool IsTarGz(string name)
        => name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);

    public static bool IsArchive(string name) => IsZip(name) || IsTarGz(name);

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
            await PromptUploadBackAsync();
        });
    }

    /// <summary>编辑后询问是否回传覆盖（不再自动上传）。</summary>
    private async Task PromptUploadBackAsync()
    {
        var local = _editLocalPath;
        var remote = _editRemotePath;
        if (local is null || remote is null || !File.Exists(local))
            return;

        var dispatcher = Application.Current?.Dispatcher;
        var yes = false;
        if (dispatcher is not null)
        {
            yes = dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                $"文件已被修改：\n{Path.GetFileName(remote)}\n\n是否回传并覆盖服务器上的文件？",
                "回传修改", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question)
                == System.Windows.MessageBoxResult.Yes);
        }

        // 无论选择如何，本次编辑会话不再重复提示
        StopEditWatch();
        if (yes)
            await UploadEditBackAsync();
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
