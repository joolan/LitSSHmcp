using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.RemoteFiles;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public sealed class RemoteFileEditorViewModel : INotifyPropertyChanged
{
    private readonly ISshService _ssh;
    private readonly IAuditLogService _audit;
    private readonly RemoteTextFileService _files;

    public RemoteFileEditorViewModel(SshServerConfig server, ISshService ssh, IAuditLogService? audit = null)
    {
        Server = server;
        _ssh = ssh;
        _audit = audit ?? new AuditLogService();
        _files = new RemoteTextFileService(ssh);
        try { _ = _audit.InitializeAsync(); } catch { /* 审计不可用不阻止编辑 */ }
        LoadRecentFiles();
    }

    public SshServerConfig Server { get; }
    public ObservableCollection<RemoteFileEditTab> Tabs { get; } = new();
    public ObservableCollection<string> RecentFiles { get; } = new();

    private RemoteFileEditTab? _selectedTab;
    public RemoteFileEditTab? SelectedTab
    {
        get => _selectedTab;
        set { if (_selectedTab == value) return; _selectedTab = value; Raise(); }
    }

    private string _statusMessage = "在线文件编辑器";
    public string StatusMessage
    {
        get => _statusMessage;
        private set { if (_statusMessage == value) return; _statusMessage = value; Raise(); }
    }

    public event Action<RemoteFileEditTab>? TabLoaded;
    public event Action<string>? FileSaved;

    public async Task<RemoteFileEditTab?> OpenExistingAsync(RemoteFileItem item, CancellationToken ct = default)
    {
        var existing = Tabs.FirstOrDefault(t =>
            !t.IsNew && string.Equals(t.RemotePath, item.FullName, StringComparison.Ordinal));
        if (existing is not null)
        {
            SelectedTab = existing;
            return existing;
        }

        if (item.IsDirectory)
        {
            StatusMessage = "不能把目录当作文件编辑。";
            return null;
        }

        if (item.IsSymbolicLink)
        {
            StatusMessage = "符号链接文件暂不支持在线编辑。";
            return null;
        }

        if (item.Size > RemoteTextFileService.HardLimitBytes)
        {
            StatusMessage = $"文件过大：{FormatSize(item.Size)}，超过 10 MiB 硬限制。";
            return null;
        }

        var tab = new RemoteFileEditTab(ParentOf(item.FullName))
        {
            RemotePath = item.FullName,
            IsLoading = true,
            Status = "加载中…"
        };
        Tabs.Add(tab);
        SelectedTab = tab;
        StatusMessage = "正在加载 " + item.Name + "…";

        try
        {
            var read = await _files.ReadAsync(Server, item.FullName, item.Size, ct);
            if (!read.Success)
            {
                tab.Status = read.Message;
                StatusMessage = read.Message;
                tab.IsLoading = false;
                TabLoaded?.Invoke(tab);
                return tab;
            }

            tab.OriginalBytes = read.Bytes;
            tab.Encoding = read.Encoding;
            tab.HasBom = read.HasBom;
            tab.LineEnding = read.LineEnding;
            tab.Snapshot = read.Snapshot;
            tab.Content = read.Content;
            tab.IsDirty = false;
            tab.Status = "已加载";
            AddRecentFile(item.FullName);
            if (!string.IsNullOrWhiteSpace(read.Message))
                StatusMessage = read.Message;
            else
                StatusMessage = $"已加载 {item.Name}";
            TabLoaded?.Invoke(tab);
            return tab;
        }
        catch (Exception ex)
        {
            tab.Status = "加载失败：" + ex.Message;
            StatusMessage = tab.Status;
            TabLoaded?.Invoke(tab);
            return tab;
        }
        finally
        {
            tab.IsLoading = false;
        }
    }

    public RemoteFileEditTab OpenBlank(string directory)
    {
        var tab = new RemoteFileEditTab(directory)
        {
            IsNew = true,
            Status = "未命名文件，保存时填写文件名",
            LineEnding = "LF"
        };
        Tabs.Add(tab);
        SelectedTab = tab;
        StatusMessage = "新建文件：填写内容后保存并命名";
        return tab;
    }

    public async Task<RemoteTextFileSaveResult> SaveTabAsync(
        RemoteFileEditTab tab,
        string content,
        bool force = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tab.RemotePath))
        {
            var missing = RemoteTextFileSaveResult.Blocked("请先为文件命名。");
            tab.Status = missing.Message;
            return missing;
        }

        tab.IsSaving = true;
        tab.Status = "保存中…";
        StatusMessage = "正在保存 " + tab.DisplayName + "…";
        try
        {
            var result = await _files.SaveAsync(
                Server, tab.RemotePath, content, tab.Encoding, tab.HasBom, tab.LineEnding,
                tab.Snapshot, force, tab.IsNew, ct);

            if (result.Success)
            {
                tab.Snapshot = result.Snapshot;
                tab.OriginalBytes = tab.Encoding.GetBytes(
                    RemoteTextFileService.NormalizeLineEnding(content, tab.LineEnding));
                tab.IsNew = false;
                tab.IsDirty = false;
                tab.Status = result.Message;
                StatusMessage = result.Message;
                if (!string.IsNullOrWhiteSpace(tab.RemotePath))
                    AddRecentFile(tab.RemotePath);
                if (result.MetadataChanged)
                    StatusMessage = "保存成功，但文件权限/属主发生变化：" + tab.DisplayName;
                FileSaved?.Invoke(tab.Directory);
            }
            else
            {
                tab.Status = result.Message;
                StatusMessage = result.Message;
            }

            await AuditSaveAsync(tab, result);
            return result;
        }
        catch (Exception ex)
        {
            var fail = RemoteTextFileSaveResult.Blocked("保存失败：" + ex.Message);
            tab.Status = fail.Message;
            StatusMessage = fail.Message;
            await AuditSaveAsync(tab, fail);
            return fail;
        }
        finally
        {
            tab.IsSaving = false;
        }
    }

    public Task<RemoteTextFileSnapshot?> GetSnapshotAsync(
        RemoteFileEditTab tab, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(tab.RemotePath)
            ? Task.FromResult<RemoteTextFileSnapshot?>(null)
            : _files.GetSnapshotAsync(Server, tab.RemotePath, ct);

    public Task<RemoteTextFileReadResult> ReadRemoteVersionAsync(
        RemoteFileEditTab tab, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(tab.RemotePath)
            ? Task.FromResult(RemoteTextFileReadResult.Fail("新文件还没有服务器版本。"))
            : _files.ReadAsync(Server, tab.RemotePath, tab.Snapshot?.Size, ct);

    public async Task<RemoteFileEditTab> ReloadTabAsync(RemoteFileEditTab tab, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tab.RemotePath))
            return tab;

        tab.IsLoading = true;
        tab.Status = "重新加载中…";
        StatusMessage = "正在重新加载 " + tab.DisplayName + "…";
        try
        {
            var read = await _files.ReadAsync(Server, tab.RemotePath, tab.Snapshot?.Size, ct);
            if (!read.Success)
            {
                tab.Status = read.Message;
                StatusMessage = read.Message;
                return tab;
            }

            tab.OriginalBytes = read.Bytes;
            tab.Encoding = read.Encoding;
            tab.HasBom = read.HasBom;
            tab.LineEnding = read.LineEnding;
            tab.Snapshot = read.Snapshot;
            tab.Content = read.Content;
            tab.IsDirty = false;
            tab.Status = "已重新加载";
            StatusMessage = tab.Status;
            TabLoaded?.Invoke(tab);
            return tab;
        }
        catch (Exception ex)
        {
            tab.Status = "重新加载失败：" + ex.Message;
            StatusMessage = tab.Status;
            return tab;
        }
        finally
        {
            tab.IsLoading = false;
        }
    }

    public async Task<bool> ChangeEncodingAsync(RemoteFileEditTab tab, Encoding encoding)
    {
        if (tab.OriginalBytes.Length == 0)
        {
            tab.Encoding = encoding;
            tab.HasBom = encoding.GetPreamble().Length > 0;
            return false;
        }

        try
        {
            tab.Encoding = encoding;
            tab.HasBom = encoding.GetPreamble().Length > 0;
            tab.Content = RemoteTextFileService.DecodeBytes(tab.OriginalBytes, encoding);
            tab.IsDirty = true;
            tab.Status = "已切换编码：" + encoding.WebName;
            StatusMessage = tab.Status;
            TabLoaded?.Invoke(tab);
            return true;
        }
        catch (Exception ex)
        {
            tab.Status = "编码切换失败：" + ex.Message;
            StatusMessage = tab.Status;
            return false;
        }
    }

    public Task<IReadOnlyList<string>> ListBackupsAsync(
        RemoteFileEditTab tab, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(tab.RemotePath)
            ? Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>())
            : _files.ListBackupsAsync(Server, tab.RemotePath, ct);

    public Task<RemoteTextFileReadResult> ReadBackupAsync(
        string backupPath, CancellationToken ct = default)
        => _files.ReadAsync(Server, backupPath, null, ct);

    public Task<CommandResult> RestoreBackupAsync(
        RemoteFileEditTab tab, string backupPath, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(tab.RemotePath)
            ? Task.FromResult(new CommandResult { Success = false, Error = "文件尚未命名。" })
            : _files.RestoreBackupAsync(Server, backupPath, tab.RemotePath, ct);

    public void RemoveTab(RemoteFileEditTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
            return;
        Tabs.RemoveAt(index);
        if (ReferenceEquals(SelectedTab, tab))
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    public static string[] EncodingNames { get; } =
    {
        "utf-8",
        "utf-8 bom",
        "gb18030",
        "gbk",
        "utf-16 le",
        "latin1"
    };

    public static string[] LineEndingNames { get; } = { "LF", "CRLF", "CR" };

    private async Task AuditSaveAsync(RemoteFileEditTab tab, RemoteTextFileSaveResult result)
    {
        try
        {
            await _audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = Server.Id,
                ServerName = Server.Name,
                Command = "remote_file_editor: save",
                Result = result.Success ? result.Message : result.Message,
                Status = result.Success ? CommandStatus.Executed : CommandStatus.Failed,
                Category = AuditCategory.Transfer,
                Tool = "remote_file_editor",
                FilePath = tab.RemotePath,
                FileSize = result.Snapshot?.Size,
                IsFileTransfer = true,
                Decision = result.Conflict ? "conflict" : "normal",
                Timestamp = DateTime.UtcNow
            });
        }
        catch
        {
            // 审计失败不回滚文件保存
        }
    }

    private string RecentKey => "fileEditor.recent." + Server.Id;

    private void LoadRecentFiles()
    {
        try
        {
            var json = UiPrefs.GetString(RecentKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
                return;
            var values = JsonSerializer.Deserialize<string[]>(json);
            if (values is null)
                return;
            foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().Take(20))
                RecentFiles.Add(value);
        }
        catch
        {
            // 最近文件损坏时忽略
        }
    }

    private void AddRecentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        var existing = RecentFiles.FirstOrDefault(p => string.Equals(p, path, StringComparison.Ordinal));
        if (existing is not null)
            RecentFiles.Remove(existing);
        RecentFiles.Insert(0, path);
        while (RecentFiles.Count > 20)
            RecentFiles.RemoveAt(RecentFiles.Count - 1);
        try { UiPrefs.SetString(RecentKey, JsonSerializer.Serialize(RecentFiles)); } catch { /* ignore */ }
    }

    public Task<RemoteFileEditTab?> OpenRecentAsync(string path, CancellationToken ct = default)
        => OpenExistingAsync(new RemoteFileItem
        {
            Name = Path.GetFileName(path),
            FullName = path,
            Size = 0,
            IsDirectory = false
        }, ct);

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / 1024.0 / 1024.0:0.0} MiB"
            : $"{bytes / 1024.0:0.0} KiB";

    private static string ParentOf(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
