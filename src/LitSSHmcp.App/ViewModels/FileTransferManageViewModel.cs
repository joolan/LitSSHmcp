using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.FileTransfer;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public sealed class FileTransferSiteItem
{
    public FileTransferSiteItem(FileTransferSiteConfig site)
    {
        Site = site;
    }

    public FileTransferSiteItem(SshServerConfig server)
    {
        Server = server;
    }

    public FileTransferSiteConfig? Site { get; }
    public SshServerConfig? Server { get; }
    public bool IsSftp => Server is not null;
    public bool CanEdit => Site is not null;
    public bool CanDelete => Site is not null;

    public string Id => Site?.Id ?? "ssh:" + Server!.Id;
    public string Name => Site?.DisplayName ?? Server!.DisplayName;
    public string ProtocolText => IsSftp ? "SFTP" : Site!.ProtocolText;
    public string Host => Site?.Host ?? Server!.Host;
    public int Port => Site?.Port ?? Server!.Port;
    public string Username => IsSftp
        ? Server!.Username
        : Site!.AuthType == FileTransferAuthType.Anonymous ? "anonymous" : Site.Username;
    public string AuthText => IsSftp ? "SSH 认证" : Site!.AuthText;
    public string SecurityText => IsSftp ? "SSH" : Site!.IsInsecure ? "明文" : "TLS";
    public bool IsInsecure => Site?.IsInsecure == true;
    public string Group => Site?.Group ?? Server!.Group;
    public string Description => Site?.Description ?? Server!.Description ?? string.Empty;
    public string TagsText => Site?.TagsText ?? Server!.TagsText;
    public string SourceText => IsSftp ? "SSH 服务器" : "FTP 站点";
    public string LastConnectedText
    {
        get
        {
            var value = Site?.LastConnectedAt ?? Server?.LastConnectedAt;
            return value is null ? string.Empty : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
    }
}

public sealed class FileTransferManageViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _config;
    private readonly IUserDialogService _dialogs;
    private readonly IWindowService _windows;
    private readonly ISshService _ssh;
    private readonly IAuditLogService _audit;
    private readonly FtpFileService _ftp = new();
    private readonly Dictionary<string, Window> _openWindows = new();
    private FileTransferSiteItem? _selectedItem;
    private string _searchText = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _isLoading;
    private bool _isCardView = string.Equals(
        UiPrefs.GetString("fileTransfer.cardView", "false"), "true", StringComparison.OrdinalIgnoreCase);

    public FileTransferManageViewModel(
        IConfigService config,
        IUserDialogService dialogs,
        IWindowService windows,
        ISshService ssh,
        IAuditLogService audit)
    {
        _config = config;
        _dialogs = dialogs;
        _windows = windows;
        _ssh = ssh;
        _audit = audit;
        FilteredSites = CollectionViewSource.GetDefaultView(Sites);
        FilteredSites.Filter = FilterSite;
    }

    public ObservableCollection<FileTransferSiteItem> Sites { get; } = new();
    public ICollectionView FilteredSites { get; }

    public FileTransferSiteItem? SelectedItem
    {
        get => _selectedItem;
        set { _selectedItem = value; OnPropertyChanged(); }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value)
                return;
            _searchText = value;
            OnPropertyChanged();
            FilteredSites.Refresh();
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set { _isLoading = value; OnPropertyChanged(); }
    }

    public bool IsCardView
    {
        get => _isCardView;
        set
        {
            if (_isCardView == value)
                return;
            _isCardView = value;
            UiPrefs.SetString("fileTransfer.cardView", value ? "true" : "false");
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsListView));
        }
    }

    public bool IsListView => !IsCardView;
    public bool IsEmpty => FilteredSites.IsEmpty;
    public bool HasItems => Sites.Count > 0;

    public async Task LoadAsync()
    {
        if (IsLoading)
            return;
        IsLoading = true;
        try
        {
            var config = await _config.LoadConfigAsync();
            Sites.Clear();
            foreach (var site in (config.FileTransferSites ?? Array.Empty<FileTransferSiteConfig>())
                         .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                Sites.Add(new FileTransferSiteItem(site));
            foreach (var server in (config.Servers ?? Array.Empty<SshServerConfig>())
                         .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                Sites.Add(new FileTransferSiteItem(server));
            FilteredSites.Refresh();
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasItems));
            StatusMessage = $"共 {Sites.Count} 个文件传输站点（FTP/FTPS {Sites.Count(s => !s.IsSftp)}，SFTP {Sites.Count(s => s.IsSftp)}）。";
        }
        catch (Exception ex)
        {
            StatusMessage = "加载文件传输站点失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task AddOrUpdateAsync(FileTransferSiteConfig site, bool isNew)
    {
        try
        {
            site.Normalize();
            var config = await _config.LoadConfigAsync();
            var list = (config.FileTransferSites ?? Array.Empty<FileTransferSiteConfig>()).ToList();
            var index = list.FindIndex(s => string.Equals(s.Id, site.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                list.Add(site);
            else
                list[index] = site;
            config.FileTransferSites = list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            await _config.SaveConfigAsync(config);
            await LoadAsync();
            StatusMessage = isNew ? $"已添加站点「{site.Name}」。" : $"已保存站点「{site.Name}」。";
        }
        catch (Exception ex)
        {
            StatusMessage = "保存文件传输站点失败：" + ex.Message;
            _dialogs.Error(StatusMessage, "文件传输");
        }
    }

    public async Task DeleteAsync(FileTransferSiteItem item)
    {
        if (item.Site is not { } site)
            return;
        if (!_dialogs.Confirm($"删除文件传输站点「{site.Name}」？\n\n只删除本地保存的站点配置，不会删除服务器文件。", "删除站点"))
            return;
        try
        {
            var config = await _config.LoadConfigAsync();
            config.FileTransferSites = (config.FileTransferSites ?? Array.Empty<FileTransferSiteConfig>())
                .Where(s => !string.Equals(s.Id, site.Id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            await _config.SaveConfigAsync(config);
            await LoadAsync();
            StatusMessage = $"已删除站点「{site.Name}」。";
        }
        catch (Exception ex)
        {
            StatusMessage = "删除文件传输站点失败：" + ex.Message;
            _dialogs.Error(StatusMessage, "文件传输");
        }
    }

    public async Task<FtpConnectionResult> TestAsync(FileTransferSiteItem item)
    {
        try
        {
            if (item.Server is { } server)
            {
                var ok = await _ssh.TestConnectionAsync(server);
                return new FtpConnectionResult
                {
                    Success = ok,
                    Message = ok ? "SFTP 连接成功。" : "SFTP 连接失败。"
                };
            }

            if (item.Site is { } site)
            {
                var result = await _ftp.TestConnectionAsync(site);
                site.LastConnectedAt = DateTime.UtcNow;
                StatusMessage = result.Message;
                return result;
            }
        }
        catch (Exception ex)
        {
            return new FtpConnectionResult { Success = false, Message = ex.Message };
        }

        return new FtpConnectionResult { Success = false, Message = "站点配置不完整。" };
    }

    public void Open(FileTransferSiteItem item)
    {
        if (_openWindows.TryGetValue(item.Id, out var existing) && existing.IsLoaded)
        {
            existing.Activate();
            return;
        }

        Window? window = null;
        if (item.Server is { } server)
        {
            window = _windows.CreateSftpManagerWindow(new SftpManagerViewModel(server, _ssh));
        }
        else if (item.Site is { } site)
        {
            window = _windows.CreateFtpManagerWindow(
                new FtpManagerViewModel(site, _ftp, _audit, _dialogs));
        }

        if (window is null)
            return;

        window.Closed += (_, _) => _openWindows.Remove(item.Id);
        _openWindows[item.Id] = window;
        window.Show();
    }

    private bool FilterSite(object item)
    {
        if (item is not FileTransferSiteItem site || string.IsNullOrWhiteSpace(SearchText))
            return true;
        var keyword = SearchText.Trim();
        return Contains(site.Name, keyword)
            || Contains(site.Host, keyword)
            || Contains(site.Username, keyword)
            || Contains(site.ProtocolText, keyword)
            || Contains(site.Group, keyword)
            || Contains(site.Description, keyword)
            || Contains(site.TagsText, keyword)
            || Contains(site.SourceText, keyword);

        static bool Contains(string? value, string keyword)
            => value?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
