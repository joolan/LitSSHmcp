using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Toolbox;

namespace LitSSHmcp.App.ViewModels;

public sealed class PasswordItemRow : INotifyPropertyChanged
{
    private bool _passwordVisible;

    public PasswordItemRow(PasswordItem model) => Model = model;

    public event PropertyChangedEventHandler? PropertyChanged;

    public PasswordItem Model { get; }
    public Guid Id => Model.Id;
    public string Title => string.IsNullOrWhiteSpace(Model.Title) ? "(未命名)" : Model.Title;
    public string Username => Model.Username;
    public string Url => Model.Url;
    public string Notes => Model.Notes;
    public string GroupText => string.IsNullOrWhiteSpace(Model.Group) ? "未分组" : Model.Group;
    public string TypeText => PasswordItemTypeText.For(Model.Type);
    public string TagsText => Model.Tags.Count == 0 ? string.Empty : string.Join(" · ", Model.Tags);
    public bool IsFavorite => Model.IsFavorite;
    public bool HasTotp => Model.Totp is not null;
    public string SecurityPhone => Model.SecurityPhone;
    public string RecoveryEmail => Model.RecoveryEmail;
    public IReadOnlyList<PasswordCustomField> CustomFields => Model.CustomFields;
    public IReadOnlyList<PasswordAttachment> Attachments => Model.Attachments;
    public IReadOnlyList<PasswordHistoryEntry> PasswordHistory => Model.PasswordHistory ?? new();
    public string AttachmentCountText => Model.Attachments.Count == 0 ? "无附件" : $"{Model.Attachments.Count} 个附件";

    public bool PasswordVisible
    {
        get => _passwordVisible;
        set
        {
            if (_passwordVisible == value)
                return;
            _passwordVisible = value;
            Raise(nameof(PasswordVisible));
            Raise(nameof(PasswordDisplay));
            Raise(nameof(PasswordActionText));
        }
    }

    public string PasswordActionText => PasswordVisible ? "隐藏" : "显示";

    public string PasswordDisplay => PasswordVisible
        ? (string.IsNullOrEmpty(Model.Password) ? "(空)" : Model.Password)
        : "••••••••";

    public void Refresh()
    {
        Raise(nameof(Title));
        Raise(nameof(Username));
        Raise(nameof(Url));
        Raise(nameof(Notes));
        Raise(nameof(GroupText));
        Raise(nameof(TypeText));
        Raise(nameof(TagsText));
        Raise(nameof(IsFavorite));
        Raise(nameof(HasTotp));
        Raise(nameof(SecurityPhone));
        Raise(nameof(RecoveryEmail));
        Raise(nameof(CustomFields));
        Raise(nameof(Attachments));
        Raise(nameof(AttachmentCountText));
        Raise(nameof(PasswordHistory));
        Raise(nameof(PasswordDisplay));
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class PasswordVaultViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PasswordVaultService _service;
    private readonly DispatcherTimer _timer;
    private readonly List<PasswordItemRow> _all = new();
    private PasswordVaultSession? _session;
    private PasswordItemRow? _selectedItem;
    private DateTimeOffset _lastActivity = DateTimeOffset.Now;
    private bool _busy;
    private bool _disposed;
    private bool _updatingGroups;
    private string _selectedClipboardClear;
    private DateTimeOffset? _passwordRevealUntil;

    public PasswordVaultViewModel(PasswordVaultService service)
    {
        _service = service;
        TypeFilters = new[] { "全部", "收藏", "网站", "应用", "邮箱", "银行", "API Key", "安全笔记" };
        AutoLockOptions = new[] { "1 分钟", "3 分钟", "5 分钟", "10 分钟", "15 分钟", "永不" };
        ClipboardClearOptions = new[] { "15 秒", "30 秒", "60 秒", "永不" };
        Groups.Add("全部分组");
        var savedAutoLock = UiPrefs.GetString("passwordVault.autoLock", "5 分钟");
        _selectedAutoLock = AutoLockOptions.Contains(savedAutoLock) ? savedAutoLock : "5 分钟";
        var savedClipboard = UiPrefs.GetString("passwordVault.clipboardClear", "30 秒");
        _selectedClipboardClear = ClipboardClearOptions.Contains(savedClipboard) ? savedClipboard : "30 秒";
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        TogglePasswordCommand = new RelayCommand(_ => TogglePassword());
        CopyUsernameCommand = new RelayCommand(_ => _ = CopyUsernameAsync());
        CopyPasswordCommand = new RelayCommand(_ => _ = CopyPasswordAsync());
        CopyTotpCommand = new RelayCommand(_ => _ = CopyTotpAsync());
        ToggleFavoriteCommand = new RelayCommand(_ => _ = ToggleFavoriteAsync());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PasswordItemRow> Items { get; } = new();
    public ObservableCollection<string> Groups { get; } = new();
    public IReadOnlyList<string> TypeFilters { get; }
    public IReadOnlyList<string> AutoLockOptions { get; }
    public IReadOnlyList<string> ClipboardClearOptions { get; }

    public ICommand TogglePasswordCommand { get; }
    public ICommand CopyUsernameCommand { get; }
    public ICommand CopyPasswordCommand { get; }
    public ICommand CopyTotpCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }

    public bool HasVault => _service.Exists;
    public bool IsUnlocked => _session is { IsLocked: false };

    public bool IsBusy
    {
        get => _busy;
        set { if (_busy != value) { _busy = value; Raise(); } }
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        private set { if (_statusMessage != value) { _statusMessage = value; Raise(); } }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            Touch();
            Raise();
            ApplyFilter();
        }
    }

    private string _selectedFilter = "全部";
    public string SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            value ??= "全部";
            if (_selectedFilter == value) return;
            _selectedFilter = value;
            Touch();
            Raise();
            ApplyFilter();
        }
    }

    private string _selectedGroup = "全部分组";
    public string SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (_updatingGroups)
            {
                _selectedGroup = value ?? "全部分组";
                return;
            }
            value ??= "全部分组";
            if (_selectedGroup == value) return;
            _selectedGroup = value;
            Touch();
            Raise();
            ApplyFilter();
        }
    }

    private string _selectedAutoLock;
    public string SelectedAutoLock
    {
        get => _selectedAutoLock;
        set
        {
            value ??= "5 分钟";
            if (_selectedAutoLock == value) return;
            _selectedAutoLock = value;
            UiPrefs.SetString("passwordVault.autoLock", value);
            Raise();
        }
    }

    public string SelectedClipboardClear
    {
        get => _selectedClipboardClear;
        set
        {
            value ??= "30 秒";
            if (_selectedClipboardClear == value) return;
            _selectedClipboardClear = value;
            UiPrefs.SetString("passwordVault.clipboardClear", value);
            Raise();
        }
    }

    public PasswordItemRow? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (ReferenceEquals(_selectedItem, value))
                return;
            if (_selectedItem is not null)
                _selectedItem.PasswordVisible = false;
            _passwordRevealUntil = null;
            _selectedItem = value;
            Raise();
            Raise(nameof(HasSelection));
            UpdateTotp();
        }
    }

    public bool HasSelection => _selectedItem is not null;

    private string _totpCode = string.Empty;
    public string TotpCode
    {
        get => _totpCode;
        private set { if (_totpCode != value) { _totpCode = value; Raise(); } }
    }

    private string _totpRemaining = string.Empty;
    public string TotpRemaining
    {
        get => _totpRemaining;
        private set { if (_totpRemaining != value) { _totpRemaining = value; Raise(); } }
    }

    private double _totpProgress;
    public double TotpProgress
    {
        get => _totpProgress;
        private set { if (Math.Abs(_totpProgress - value) > 0.001) { _totpProgress = value; Raise(); } }
    }

    private string _totpCaption = string.Empty;
    public string TotpCaption
    {
        get => _totpCaption;
        private set { if (_totpCaption != value) { _totpCaption = value; Raise(); } }
    }

    public bool HasTotp => _selectedItem?.Model.Totp is not null;
    public bool IsHotp => _selectedItem?.Model.Totp?.IsHotp == true;
    public string HotpCounterText => IsHotp ? "HOTP #" + (_selectedItem?.Model.Totp?.Counter ?? 0) : string.Empty;
    public bool IsTotpUrgent { get; private set; }

    public async Task<bool> CreateAsync(string name, string password)
    {
        IsBusy = true;
        try
        {
            var session = await _service.CreateAsync(name, password);
            StartSession(session);
            StatusMessage = "密码保管箱已创建。";
            return true;
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
        IsBusy = true;
        try
        {
            var session = await _service.UnlockAsync(password);
            StartSession(session);
            StatusMessage = "已解锁。";
            return true;
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

    public async Task<string?> ChangePasswordAsync(string oldPassword, string newPassword)
    {
        try
        {
            await _service.ChangePasswordAsync(oldPassword, newPassword);
            StatusMessage = "主密码已修改。";
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public async Task<string?> ExportPackageAsync(string packagePath)
    {
        if (_session is null)
            return "密码保管箱已锁定。";
        try
        {
            await _session.SaveIndexAsync();
            await _service.ExportPackageAsync(packagePath);
            StatusMessage = "加密备份已导出。";
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public async Task<string?> RestorePackageAsync(string packagePath, string password)
    {
        Lock();
        try
        {
            var backup = await _service.RestorePackageAsync(packagePath, password);
            StatusMessage = backup is null
                ? "备份已恢复，请重新解锁。"
                : "备份已恢复，原密码箱已备份到：" + backup;
            return null;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            return ex.Message;
        }
    }

    public async Task SaveItemAsync(PasswordItem item)
    {
        if (_session is null)
            return;
        await _session.SaveItemAsync(item);
        StatusMessage = "已保存。";
        Reload();
        var row = _all.FirstOrDefault(r => r.Id == item.Id);
        if (row is not null)
            SelectedItem = row;
    }

    public async Task DeleteSelectedAsync()
    {
        if (_session is null || _selectedItem is null)
            return;
        var id = _selectedItem.Id;
        await _session.DeleteItemAsync(_selectedItem.Model);
        StatusMessage = "已删除。";
        Reload();
        SelectedItem = _all.FirstOrDefault(r => r.Id == id);
    }

    public async Task AddAttachmentAsync(PasswordItem item, string path)
    {
        if (_session is null)
            return;
        await _session.AddAttachmentAsync(item, path);
        _selectedItem?.Refresh();
        StatusMessage = "附件已加密保存。";
    }

    public async Task ExportAttachmentAsync(PasswordItem item, PasswordAttachment attachment, string path)
    {
        if (_session is null)
            return;
        await _session.ExportAttachmentAsync(item, attachment, path);
        StatusMessage = "附件已导出（明文）。";
    }

    public async Task<byte[]> ReadAttachmentBytesAsync(PasswordItem item, PasswordAttachment attachment)
    {
        if (_session is null)
            throw new InvalidOperationException("密码保管箱已锁定。");
        return await _session.ReadAttachmentBytesAsync(item, attachment);
    }

    public async Task DeleteAttachmentAsync(PasswordItem item, PasswordAttachment attachment)
    {
        if (_session is null)
            return;
        await _session.DeleteAttachmentAsync(item, attachment);
        _selectedItem?.Refresh();
        StatusMessage = "附件已删除。";
    }

    public async Task ToggleFavoriteAsync()
    {
        if (_session is null || _selectedItem is null)
            return;
        _selectedItem.Model.IsFavorite = !_selectedItem.Model.IsFavorite;
        await _session.SaveItemAsync(_selectedItem.Model);
        _selectedItem.Refresh();
        ApplyFilter();
        UpdateTotp();
    }

    public async Task CopyUsernameAsync()
    {
        if (_selectedItem is null || string.IsNullOrWhiteSpace(_selectedItem.Model.Username))
            return;
        if (await ClipboardHelper.SetSensitiveTextAsync(_selectedItem.Model.Username, ClipboardClearDelay()))
        {
            await MarkUsedAsync();
            StatusMessage = CopyStatus("用户名");
        }
    }

    public async Task CopyPasswordAsync()
    {
        if (_selectedItem is null || string.IsNullOrEmpty(_selectedItem.Model.Password))
            return;
        if (await ClipboardHelper.SetSensitiveTextAsync(_selectedItem.Model.Password, ClipboardClearDelay()))
        {
            await MarkUsedAsync();
            StatusMessage = CopyStatus("密码");
        }
    }

    public async Task CopyHistoryPasswordAsync(PasswordHistoryEntry entry)
    {
        if (await ClipboardHelper.SetSensitiveTextAsync(entry.Password, ClipboardClearDelay()))
            StatusMessage = CopyStatus("历史密码");
    }

    public async Task RestoreHistoryPasswordAsync(PasswordHistoryEntry entry)
    {
        if (_session is null || _selectedItem is null)
            return;
        if (string.Equals(_selectedItem.Model.Password, entry.Password, StringComparison.Ordinal))
            return;

        var updated = _selectedItem.Model.Clone();
        updated.PasswordHistory = updated.PasswordHistory
            .Where(h => !(h.ChangedAt == entry.ChangedAt &&
                          string.Equals(h.Password, entry.Password, StringComparison.Ordinal)))
            .ToList();
        updated.Password = entry.Password;
        await SaveItemAsync(updated);
        StatusMessage = "已恢复历史密码。";
    }

    public async Task CopyUrlAsync()
    {
        if (_selectedItem is null || string.IsNullOrWhiteSpace(_selectedItem.Model.Url))
            return;
        if (await ClipboardHelper.SetSensitiveTextAsync(_selectedItem.Model.Url, ClipboardClearDelay()))
            StatusMessage = CopyStatus("网址");
    }

    public async Task CopyTotpAsync()
    {
        if (string.IsNullOrEmpty(TotpCode))
            return;
        if (await ClipboardHelper.SetSensitiveTextAsync(TotpCode.Replace(" ", string.Empty),
                ClipboardClearDelay()))
        {
            await MarkUsedAsync();
            StatusMessage = CopyStatus("验证码");
        }
    }

    public void TogglePassword()
    {
        if (_selectedItem is null)
            return;
        _selectedItem.PasswordVisible = !_selectedItem.PasswordVisible;
        _passwordRevealUntil = _selectedItem.PasswordVisible
            ? DateTimeOffset.Now.AddSeconds(15)
            : null;
        Touch();
    }

    public void Lock()
    {
        if (_session is null)
            return;
        _session.Dispose();
        _session = null;
        ClipboardHelper.ClearTrackedIfUnchanged();
        _all.Clear();
        Items.Clear();
        Groups.Clear();
        Groups.Add("全部分组");
        SelectedItem = null;
        TotpCode = string.Empty;
        TotpRemaining = string.Empty;
        TotpProgress = 0;
        TotpCaption = string.Empty;
        IsTotpUrgent = false;
        StatusMessage = "已锁定。";
        Raise(nameof(IsUnlocked));
        Raise(nameof(HasVault));
        Raise(nameof(HasTotp));
        Raise(nameof(IsHotp));
        Raise(nameof(HotpCounterText));
        Raise(nameof(IsTotpUrgent));
        Raise(nameof(HasSelection));
    }

    public void Touch() => _lastActivity = DateTimeOffset.Now;

    public void ApplyFilter()
    {
        var keyword = _searchText.Trim();
        IEnumerable<PasswordItemRow> query = _all;
        if (_selectedFilter == "收藏")
            query = query.Where(r => r.Model.IsFavorite);
        else if (!string.IsNullOrEmpty(_selectedFilter) && _selectedFilter != "全部")
        {
            query = query.Where(r => PasswordItemTypeText.For(r.Model.Type) == _selectedFilter);
        }

        if (!string.IsNullOrEmpty(_selectedGroup) && _selectedGroup != "全部分组")
            query = query.Where(r => r.GroupText == _selectedGroup);

        if (keyword.Length > 0)
        {
            query = query.Where(r =>
                r.Title.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
                r.Username.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
                r.Url.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
                r.GroupText.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
                r.TagsText.Contains(keyword, StringComparison.CurrentCultureIgnoreCase));
        }

        var selectedId = _selectedItem?.Id;
        Items.Clear();
        foreach (var row in query.OrderByDescending(r => r.Model.IsFavorite).ThenBy(r => r.Title))
            Items.Add(row);

        if (selectedId is { } id)
            SelectedItem = Items.FirstOrDefault(r => r.Id == id);
        else if (SelectedItem is null)
            SelectedItem = Items.FirstOrDefault();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _session?.Dispose();
        _session = null;
    }

    private void StartSession(PasswordVaultSession session)
    {
        _session?.Dispose();
        _session = session;
        _lastActivity = DateTimeOffset.Now;
        Reload();
        Raise(nameof(IsUnlocked));
        Raise(nameof(HasVault));
    }

    private void Reload()
    {
        _all.Clear();
        if (_session is not null)
            _all.AddRange(_session.Items
                .OrderByDescending(i => i.IsFavorite)
                .ThenBy(i => i.Title)
                .Select(i => new PasswordItemRow(i)));

        var groups = _all.Select(r => r.GroupText).Distinct().OrderBy(g => g).ToArray();
        var current = _selectedGroup;
        _updatingGroups = true;
        try
        {
            Groups.Clear();
            Groups.Add("全部分组");
            foreach (var group in groups)
                Groups.Add(group);
        }
        finally
        {
            _updatingGroups = false;
        }

        _selectedGroup = current is not null && Groups.Contains(current) ? current : "全部分组";
        Raise(nameof(SelectedGroup));
        ApplyFilter();
    }

    private async Task MarkUsedAsync()
    {
        if (_session is null || _selectedItem is null)
            return;
        _selectedItem.Model.LastUsedAt = DateTimeOffset.Now;
        await _session.TouchItemAsync(_selectedItem.Model);
    }

    private void OnTick()
    {
        if (_session is null)
            return;

        UpdateTotp();
        if (_passwordRevealUntil is { } revealUntil && DateTimeOffset.Now >= revealUntil)
        {
            if (_selectedItem is not null)
                _selectedItem.PasswordVisible = false;
            _passwordRevealUntil = null;
        }

        var minutes = AutoLockMinutes();
        if (minutes > 0 && DateTimeOffset.Now - _lastActivity >= TimeSpan.FromMinutes(minutes))
            Lock();
    }

    private void UpdateTotp()
    {
        var config = _selectedItem?.Model.Totp;
        if (config is null || string.IsNullOrWhiteSpace(config.Secret))
        {
            if (TotpCode.Length > 0)
                TotpCode = string.Empty;
            if (TotpRemaining.Length > 0)
                TotpRemaining = string.Empty;
            TotpProgress = 0;
            if (TotpCaption.Length > 0)
                TotpCaption = string.Empty;
            IsTotpUrgent = false;
            Raise(nameof(HasTotp));
            Raise(nameof(IsHotp));
            Raise(nameof(HotpCounterText));
            Raise(nameof(IsTotpUrgent));
            return;
        }

        var code = TotpGenerator.Generate(config);
        TotpCode = string.IsNullOrEmpty(code.Code) ? "无效 Secret" : FormatCode(code.Code);
        TotpRemaining = config.IsHotp ? $"#{config.Counter}" : $"{code.RemainingSeconds}s";
        TotpProgress = config.IsHotp ? 1 : code.Progress;
        IsTotpUrgent = !config.IsHotp && code.RemainingSeconds <= 5;
        TotpCaption = string.Join(" / ", new[] { config.Issuer, config.Account }
            .Where(v => !string.IsNullOrWhiteSpace(v)));
        Raise(nameof(HasTotp));
        Raise(nameof(IsHotp));
        Raise(nameof(HotpCounterText));
        Raise(nameof(IsTotpUrgent));
    }

    public async Task AdvanceHotpAsync()
    {
        if (_session is null || _selectedItem?.Model.Totp is not { IsHotp: true } config)
            return;
        config.Counter = Math.Max(0, config.Counter) + 1;
        await _session.SaveItemAsync(_selectedItem.Model);
        _selectedItem.Refresh();
        UpdateTotp();
        StatusMessage = "HOTP 计数器已推进到 #" + config.Counter;
    }

    private int AutoLockMinutes() => _selectedAutoLock switch
    {
        "1 分钟" => 1,
        "3 分钟" => 3,
        "5 分钟" => 5,
        "10 分钟" => 10,
        "15 分钟" => 15,
        _ => 0
    };

    private static string FormatCode(string code) =>
        code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;

    private TimeSpan? ClipboardClearDelay() => _selectedClipboardClear switch
    {
        "15 秒" => TimeSpan.FromSeconds(15),
        "30 秒" => TimeSpan.FromSeconds(30),
        "60 秒" => TimeSpan.FromSeconds(60),
        _ => null
    };

    private string CopyStatus(string what)
    {
        var delay = ClipboardClearDelay();
        return delay is null
            ? $"{what}已复制。"
            : $"{what}已复制，{(int)delay.Value.TotalSeconds} 秒后自动清空剪贴板。";
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
