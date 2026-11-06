using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Toolbox;

namespace LitSSHmcp.App.ViewModels;

public sealed class EncryptedVaultViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly EncryptedVaultService _service;
    private readonly DispatcherTimer _autoLockTimer;
    private VaultSession? _session;
    private VaultDescriptor? _selectedVault;
    private DateTimeOffset _lastActivity = DateTimeOffset.Now;
    private bool _busy;
    private CancellationTokenSource? _operationCts;
    private double _progressPercent;
    private string _progressText = string.Empty;
    private int _autoLockMinutes = 5;
    private string _autoLockMode = "5 分钟";

    public EncryptedVaultViewModel(EncryptedVaultService? service = null)
    {
        _service = service ?? new EncryptedVaultService();
        _autoLockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autoLockTimer.Tick += (_, _) => CheckAutoLock();
        _autoLockTimer.Start();
        _autoLockMinutes = Math.Max(0, (int)UiPrefs.GetDouble("vault.autoLockMinutes", 5));
        _autoLockMode = FormatAutoLockMode(_autoLockMinutes);
    }

    public int AutoLockMinutes => _autoLockMinutes;
    public IReadOnlyList<string> AutoLockOptions { get; } = new[] { "1 分钟", "5 分钟", "15 分钟", "30 分钟", "不自动锁定" };

    public string AutoLockMode
    {
        get => _autoLockMode;
        set
        {
            if (_autoLockMode == value) return;
            _autoLockMode = value;
            _autoLockMinutes = ParseAutoLockMode(value);
            UiPrefs.SetDouble("vault.autoLockMinutes", _autoLockMinutes);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoLockMinutes));
            OnPropertyChanged(nameof(AutoLockText));
        }
    }

    public string AutoLockText => _autoLockMinutes <= 0 ? "不自动锁定" : $"{_autoLockMinutes} 分钟无操作自动锁定";

    private static int ParseAutoLockMode(string value) => value switch
    {
        "1 分钟" => 1,
        "5 分钟" => 5,
        "15 分钟" => 15,
        "30 分钟" => 30,
        _ => 0
    };

    private static string FormatAutoLockMode(int minutes) => minutes switch
    {
        1 => "1 分钟",
        5 => "5 分钟",
        15 => "15 分钟",
        30 => "30 分钟",
        _ => "不自动锁定"
    };
    public ObservableCollection<VaultDescriptor> Vaults { get; } = new();
    public ObservableCollection<VaultEntryView> Entries { get; } = new();

    public VaultDescriptor? SelectedVault
    {
        get => _selectedVault;
        set
        {
            if (Equals(_selectedVault, value))
                return;
            LockCore(silent: true);
            _selectedVault = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VaultName));
            NotifyState();
            StatusMessage = value is null ? "请选择或新建文件柜。" : "文件柜已锁定，请输入主密码。";
        }
    }

    public string VaultName => _selectedVault?.Name ?? "未选择文件柜";
    public bool HasVaults => Vaults.Count > 0;
    public bool HasSelection => _selectedVault is not null;
    public bool IsUnlocked => _session is not null;
    public bool IsLocked => _selectedVault is not null && _session is null;
    public bool HasEntries => Entries.Count > 0;

    private string _statusMessage = "正在加载文件柜…";
    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            _busy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanCancel));
        }
    }

    public bool CanCancel => IsBusy;
    public double ProgressPercent
    {
        get => _progressPercent;
        private set { _progressPercent = value; OnPropertyChanged(); }
    }

    public string ProgressText
    {
        get => _progressText;
        private set { _progressText = value; OnPropertyChanged(); }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged();
            RefreshEntries();
            Touch();
        }
    }

    public async Task InitializeAsync()
    {
        await LoadVaultsAsync();
        StatusMessage = HasVaults ? "请选择文件柜并输入主密码。" : "还没有文件柜，请先新建。";
    }

    public async Task LoadVaultsAsync()
    {
        var selectedId = _selectedVault?.Id;
        Vaults.Clear();
        foreach (var vault in _service.ListVaults())
            Vaults.Add(vault);

        SelectedVault = Vaults.FirstOrDefault(v => v.Id == selectedId) ?? Vaults.FirstOrDefault();
        OnPropertyChanged(nameof(HasVaults));
        NotifyState();
    }

    public async Task<bool> CreateVaultAsync(string name, string password)
    {
        IsBusy = true;
        try
        {
            Touch();
            var descriptor = await _service.CreateVaultAsync(name, password);
            await LoadVaultsAsync();
            SelectedVault = Vaults.FirstOrDefault(v => v.Id == descriptor.Id);
            return await UnlockAsync(password);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> UnlockAsync(string password)
    {
        if (_selectedVault is null)
            return false;

        IsBusy = true;
        try
        {
            _session = await _service.UnlockAsync(_selectedVault.Id, password);
            RefreshEntries();
            StatusMessage = $"已解锁：{_selectedVault.Name}（{AutoLockText}）";
            Touch();
            NotifyState();
            return true;
        }
        catch (VaultUnlockException ex)
        {
            StatusMessage = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            StatusMessage = "解锁失败：" + ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Lock()
    {
        if (_session is null)
            return;
        LockCore(silent: false);
    }

    private void LockCore(bool silent)
    {
        _session?.Dispose();
        _session = null;
        Entries.Clear();
        OnPropertyChanged(nameof(HasEntries));
        NotifyState();
        if (!silent)
            StatusMessage = "文件柜已锁定。";
    }

    public async Task<VaultImportResult?> ImportPathsAsync(IEnumerable<string> paths)
    {
        if (_session is null)
            return null;

        var ct = BeginOperation();
        try
        {
            Touch();
            var progress = new Progress<VaultProgress>(UpdateProgress);
            var result = await _session.ImportPathsAsync(paths, progress, ct);
            RefreshEntries();
            StatusMessage = result.Cancelled
                ? $"已取消导入，已导入 {result.Imported} 个文件。"
                : $"已导入 {result.Imported} 个文件" +
                  (result.Skipped > 0 ? $"，跳过 {result.Skipped} 个" : string.Empty) +
                  (result.Errors.Count > 0 ? $"，{result.Errors.Count} 个失败" : string.Empty);
            return result;
        }
        catch (OperationCanceledException)
        {
            RefreshEntries();
            StatusMessage = "已取消导入。";
            return null;
        }
        catch (Exception ex)
        {
            StatusMessage = "导入失败：" + ex.Message;
            return null;
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task<VaultExportResult?> ExportAsync(IEnumerable<VaultEntryView> entries, string destination)
    {
        if (_session is null)
            return null;

        var ct = BeginOperation();
        try
        {
            Touch();
            var progress = new Progress<VaultProgress>(UpdateProgress);
            var result = await _session.ExportAsync(entries.Select(e => e.Model), destination, progress, ct);
            StatusMessage = result.Cancelled
                ? $"已取消导出，已导出 {result.Exported} 个文件。"
                : $"已导出 {result.Exported} 个文件到 {destination}";
            return result;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消导出。";
            return null;
        }
        catch (Exception ex)
        {
            StatusMessage = "导出失败：" + ex.Message;
            return null;
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task ExportPackageAsync(string packagePath)
    {
        if (_selectedVault is null)
            return;

        var ct = BeginOperation();
        try
        {
            Touch();
            var progress = new Progress<VaultProgress>(UpdateProgress);
            await _service.ExportPackageAsync(_selectedVault.Id, packagePath, progress, ct);
            StatusMessage = "已导出加密包：" + packagePath;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消导出加密包。";
        }
        catch (Exception ex)
        {
            StatusMessage = "导出加密包失败：" + ex.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task RenameVaultAsync(VaultDescriptor vault, string newName)
    {
        try
        {
            var updated = await _service.RenameVaultAsync(vault.Id, newName);
            var index = Vaults.IndexOf(vault);
            if (index >= 0)
                Vaults[index] = updated;
            if (SelectedVault?.Id == vault.Id)
            {
                _selectedVault = updated;
                OnPropertyChanged(nameof(SelectedVault));
                OnPropertyChanged(nameof(VaultName));
            }
            StatusMessage = "已重命名文件柜：" + updated.Name;
        }
        catch (Exception ex)
        {
            StatusMessage = "重命名失败：" + ex.Message;
        }
    }

    public async Task ImportPackageAsync(string packagePath)
    {
        var ct = BeginOperation();
        try
        {
            var progress = new Progress<VaultProgress>(UpdateProgress);
            var descriptor = await _service.ImportPackageAsync(packagePath, progress, ct);
            await LoadVaultsAsync();
            SelectedVault = Vaults.FirstOrDefault(v => v.Id == descriptor.Id);
            StatusMessage = "已导入加密包，请使用原主密码解锁。";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消导入加密包。";
        }
        catch (Exception ex)
        {
            StatusMessage = "导入加密包失败：" + ex.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task<string?> ChangePasswordAsync(VaultDescriptor vault, string oldPassword, string newPassword)
    {
        try
        {
            await _service.ChangePasswordAsync(vault.Id, oldPassword, newPassword);
            StatusMessage = "主密码已修改。";
            return null;
        }
        catch (Exception ex)
        {
            return "修改主密码失败：" + ex.Message;
        }
    }

    public async Task DeleteVaultAsync(VaultDescriptor vault, string password)
    {
        try
        {
            var wasSelected = SelectedVault?.Id == vault.Id;
            if (wasSelected)
                LockCore(silent: true);
            await _service.DeleteVaultAsync(vault.Id, password);
            await LoadVaultsAsync();
            StatusMessage = "已删除文件柜：" + vault.Name;
        }
        catch (Exception ex)
        {
            StatusMessage = "删除文件柜失败：" + ex.Message;
        }
    }

    public void SetStatus(string message) => StatusMessage = message;

    public void CancelOperation()
    {
        try { _operationCts?.Cancel(); } catch { /* ignore */ }
        StatusMessage = "正在取消…";
    }

    private CancellationToken BeginOperation()
    {
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        IsBusy = true;
        ProgressPercent = 0;
        ProgressText = string.Empty;
        return _operationCts.Token;
    }

    private void EndOperation()
    {
        _operationCts?.Dispose();
        _operationCts = null;
        IsBusy = false;
        ProgressText = string.Empty;
    }

    private void UpdateProgress(VaultProgress progress)
    {
        ProgressPercent = progress.TotalBytes > 0
            ? Math.Clamp(progress.ProcessedBytes * 100.0 / progress.TotalBytes, 0, 100)
            : progress.TotalFiles > 0
                ? Math.Clamp(progress.CompletedFiles * 100.0 / progress.TotalFiles, 0, 100)
                : 0;
        ProgressText = $"{progress.CurrentFile} · {progress.CompletedFiles}/{progress.TotalFiles}";
    }

    public async Task<byte[]> ReadEntryBytesAsync(VaultEntryView entry, long maxBytes)
    {
        if (_session is null)
            throw new InvalidOperationException("文件柜已锁定。");
        return await _session.ReadFileBytesAsync(entry.Model, maxBytes);
    }

    public async Task<string?> SaveEntryBytesAsync(VaultEntryView entry, byte[] content,
        DateTimeOffset expectedModified)
    {
        if (_session is null)
            return "文件柜已锁定。";
        try
        {
            await _session.SaveFileBytesAsync(entry.Model, content, expectedModified);
            RefreshEntries();
            StatusMessage = "已保存并重新加密：" + entry.Name;
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public async Task DeleteAsync(IEnumerable<VaultEntryView> entries)
    {
        if (_session is null)
            return;
        IsBusy = true;
        try
        {
            Touch();
            var list = entries.ToArray();
            await _session.DeleteAsync(list.Select(e => e.Model));
            RefreshEntries();
            StatusMessage = $"已删除 {list.Length} 个文件。";
        }
        catch (Exception ex)
        {
            StatusMessage = "删除失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RenameAsync(VaultEntryView entry, string newName)
    {
        if (_session is null)
            return;
        Touch();
        await _session.RenameAsync(entry.Model, newName);
        RefreshEntries();
        StatusMessage = "已重命名。";
    }

    public void Touch() => _lastActivity = DateTimeOffset.Now;

    private void RefreshEntries()
    {
        Entries.Clear();
        if (_session is null)
            return;
        foreach (var entry in _session.Search(_searchText))
            Entries.Add(new VaultEntryView(entry));
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(IsUnlocked));
        OnPropertyChanged(nameof(IsLocked));
    }

    private void CheckAutoLock()
    {
        if (_session is null || AutoLockMinutes <= 0)
            return;
        if (DateTimeOffset.Now - _lastActivity >= TimeSpan.FromMinutes(AutoLockMinutes))
            Lock();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsUnlocked));
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(VaultName));
    }

    public void Dispose()
    {
        _autoLockTimer.Stop();
        LockCore(silent: true);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class VaultEntryView
{
    public VaultEntryView(VaultEntry model) => Model = model;

    public VaultEntry Model { get; }
    public string Name => Model.Name;
    public string RelativePath => Model.RelativePath.Replace('\\', '/');
    public string SizeText => FormatSize(Model.Size);
    public string ModifiedText => Model.ModifiedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.##} MB",
        >= 1024L => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} B"
    };
}
