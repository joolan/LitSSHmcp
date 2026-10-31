using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public sealed class SiteAssetRow
{
    public string ServerId { get; init; } = string.Empty;
    public string ServerName { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string Ports { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
    public string ServerType { get; init; } = string.Empty;
    public string SiteType { get; init; } = string.Empty;
    public string ConfigSource { get; init; } = string.Empty;
    public string CertificatePath { get; init; } = string.Empty;
    public string CertificateSubject { get; init; } = string.Empty;
    public string NotAfter { get; init; } = string.Empty;
    public string DaysText { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string ProbedAt { get; init; } = string.Empty;
    public string Confidence { get; init; } = string.Empty;
    public int? DaysRemaining { get; init; }
    public bool Expired { get; init; }
}

public sealed class CertificateAssetRow
{
    public string ServerId { get; init; } = string.Empty;
    public string ServerName { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Domains { get; init; } = string.Empty;
    public string NotAfter { get; init; } = string.Empty;
    public string DaysText { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string ProbedAt { get; init; } = string.Empty;
    public int? DaysRemaining { get; init; }
    public bool Expired { get; init; }
}

public sealed class SiteAssetDashboardViewModel : INotifyPropertyChanged
{
    private readonly IUserDialogService _dialogs;
    private readonly IConfigService? _config;
    private readonly ISshService? _ssh;
    private readonly IAuditLogService? _audit;
    private readonly IWindowService _windows;
    private CancellationTokenSource? _batchCts;
    private bool _batchRunning;

    public SiteAssetDashboardViewModel(IUserDialogService? dialogs = null,
        IConfigService? config = null, ISshService? ssh = null, IAuditLogService? audit = null,
        IWindowService? windows = null)
    {
        _dialogs = dialogs ?? new UserDialogService();
        _config = config;
        _ssh = ssh;
        _audit = audit;
        _windows = windows ?? new WpfWindowService();
        SitesView = CollectionViewSource.GetDefaultView(Sites);
        SitesView.Filter = FilterSite;
        CertificatesView = CollectionViewSource.GetDefaultView(Certificates);
        CertificatesView.Filter = FilterCertificate;
        RefreshCommand = new RelayCommand(_ => Load());
        ExportSitesCommand = new RelayCommand(_ => ExportSites());
        ExportCertificatesCommand = new RelayCommand(_ => ExportCertificates());
        OpenFolderCommand = new RelayCommand(_ => OpenFolder());
        BatchProbeCommand = new RelayCommand(_ => _ = SelectBatchServersAsync(),
            _ => !BatchRunning && _config is not null && _ssh is not null);
        StopBatchCommand = new RelayCommand(_ => _batchCts?.Cancel(), _ => BatchRunning);
        Load();
    }

    public ObservableCollection<SiteAssetRow> Sites { get; } = new();
    public ObservableCollection<CertificateAssetRow> Certificates { get; } = new();
    public ICollectionView SitesView { get; }
    public ICollectionView CertificatesView { get; }

    public ICommand RefreshCommand { get; }
    public ICommand ExportSitesCommand { get; }
    public ICommand ExportCertificatesCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand BatchProbeCommand { get; }
    public ICommand StopBatchCommand { get; }

    public string[] ExpiryOptions { get; } = { "全部", "已过期", "30 天内", "90 天内" };

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged();
            SitesView.Refresh();
            CertificatesView.Refresh();
        }
    }

    private string _expiryFilter = "全部";
    public string ExpiryFilter
    {
        get => _expiryFilter;
        set
        {
            if (_expiryFilter == value) return;
            _expiryFilter = value;
            OnPropertyChanged();
            SitesView.Refresh();
            CertificatesView.Refresh();
        }
    }

    private string _statusText = "就绪";
    public string StatusText
    {
        get => _statusText;
        private set { if (_statusText == value) return; _statusText = value; OnPropertyChanged(); }
    }

    private string _lastRefreshText = "—";
    public string LastRefreshText
    {
        get => _lastRefreshText;
        private set { if (_lastRefreshText == value) return; _lastRefreshText = value; OnPropertyChanged(); }
    }

    private int _totalServers;
    public int TotalServers
    {
        get => _totalServers;
        private set { if (_totalServers == value) return; _totalServers = value; OnPropertyChanged(); }
    }

    public int TotalSites => Sites.Count;
    public int TotalCertificates => Certificates.Count;
    public int Expiring30 => Certificates.Count(c => c.DaysRemaining is >= 0 and <= 30);
    public int ExpiredCertificates => Certificates.Count(c => c.Expired);
    public int Expiring90 => Certificates.Count(c => c.DaysRemaining is >= 0 and <= 90);

    private static string ResultDirectory =>
        Path.Combine(ConfigPaths.AppDataDir, "site-probes");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public bool BatchRunning
    {
        get => _batchRunning;
        private set
        {
            if (_batchRunning == value) return;
            _batchRunning = value;
            OnPropertyChanged();
            (BatchProbeCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StopBatchCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private bool _batchElevated;
    public bool BatchElevated
    {
        get => _batchElevated;
        set { if (_batchElevated == value) return; _batchElevated = value; OnPropertyChanged(); }
    }

    private string _batchStatus = string.Empty;
    public string BatchStatus
    {
        get => _batchStatus;
        private set { if (_batchStatus == value) return; _batchStatus = value; OnPropertyChanged(); }
    }

    private int _batchProgress;
    public int BatchProgress
    {
        get => _batchProgress;
        private set { if (_batchProgress == value) return; _batchProgress = value; OnPropertyChanged(); }
    }

    private int _batchTotal;
    public int BatchTotal
    {
        get => _batchTotal;
        private set { if (_batchTotal == value) return; _batchTotal = value; OnPropertyChanged(); }
    }

    private async Task SelectBatchServersAsync()
    {
        if (BatchRunning || _config is null || _ssh is null)
            return;

        AppConfig config;
        try
        {
            config = await _config.LoadConfigAsync();
        }
        catch (Exception ex)
        {
            _dialogs.Error("读取服务器配置失败：" + ex.Message, "站点资产汇总");
            return;
        }

        var servers = config.Servers.Where(server => !server.Disabled).ToList();
        if (servers.Count == 0)
        {
            _dialogs.Info("没有可探测的 SSH 服务器。", "站点资产汇总");
            return;
        }

        var selection = new BatchProbeSelectionViewModel(servers, _dialogs);
        var window = _windows.CreateBatchProbeSelectionWindow(selection);
        if (window.ShowDialog() != true)
            return;
        var selected = selection.SelectedServers;
        if (selected.Count == 0)
            return;
        await BatchProbeAsync(selected);
    }

    private async Task BatchProbeAsync(IReadOnlyList<SshServerConfig> servers)
    {
        if (BatchRunning || _ssh is null || servers.Count == 0)
            return;

        _batchCts = new CancellationTokenSource();
        BatchRunning = true;
        BatchProgress = 0;
        BatchTotal = servers.Count;
        var success = 0;
        var failed = 0;
        try
        {
            for (var i = 0; i < servers.Count; i++)
            {
                if (_batchCts.IsCancellationRequested)
                    break;
                var server = servers[i];
                BatchStatus = $"[{i + 1}/{servers.Count}] 正在探测 {server.Name}…";
                var probe = new SiteDiscoveryViewModel(server, _ssh, _dialogs, _audit);
                try
                {
                    if (await probe.RunForBatchAsync(BatchElevated))
                        success++;
                    else
                        failed++;
                }
                catch
                {
                    failed++;
                }
                BatchProgress = i + 1;
            }
            Load();
            BatchStatus = _batchCts.IsCancellationRequested
                ? $"已停止：成功 {success}，失败 {failed}"
                : $"批量探测完成：成功 {success}，失败 {failed}";
        }
        finally
        {
            BatchRunning = false;
            _batchCts.Dispose();
            _batchCts = null;
        }
    }

    private void Load()
    {
        Sites.Clear();
        Certificates.Clear();
        try
        {
            var dir = ResultDirectory;
            if (!Directory.Exists(dir))
            {
                StatusText = "尚无站点探测结果。请先在 SSH 服务器中执行 HTTP站点探测。";
                LastRefreshText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                RefreshCounts();
                return;
            }

            var snapshots = new List<(string File, SiteProbeSnapshot Snapshot)>();
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var snapshot = JsonSerializer.Deserialize<SiteProbeSnapshot>(File.ReadAllText(file), JsonOptions);
                    if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.ServerId))
                        continue;
                    if (snapshot.CreatedAt == default)
                        snapshot.CreatedAt = File.GetLastWriteTime(file);
                    snapshots.Add((file, snapshot));
                }
                catch
                {
                    // 单个损坏的结果文件不影响其他服务器
                }
            }

            var latest = snapshots
                .GroupBy(item => item.Snapshot.ServerId, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(item => item.Snapshot.CreatedAt).First())
                .ToList();
            TotalServers = latest.Count;

            foreach (var (_, snapshot) in latest)
            {
                var certByPath = snapshot.Certificates
                    .Where(c => !string.IsNullOrWhiteSpace(c.Path))
                    .GroupBy(c => c.Path, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                var siteDomainsByPath = snapshot.Sites
                    .Where(s => !string.IsNullOrWhiteSpace(s.CertificatePath))
                    .GroupBy(s => s.CertificatePath, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(s => s.Domain).Where(d => d.Length > 0).Distinct()), StringComparer.Ordinal);

                foreach (var site in snapshot.Sites)
                {
                    certByPath.TryGetValue(site.CertificatePath, out var cert);
                    var (days, expired) = CertDays(cert);
                    Sites.Add(new SiteAssetRow
                    {
                        ServerId = snapshot.ServerId,
                        ServerName = snapshot.ServerName,
                        Domain = site.Domain,
                        Ports = site.Ports,
                        Protocol = site.Protocol,
                        ServerType = site.ServerType,
                        SiteType = site.SiteType,
                        ConfigSource = site.ConfigSource,
                        CertificatePath = site.CertificatePath,
                        CertificateSubject = cert?.Subject ?? site.CertificateSubject,
                        NotAfter = FormatDate(cert?.NotAfter ?? site.CertificateExpiry),
                        DaysText = days is null ? "—" : $"{days} 天",
                        Status = CertStatus(days, expired),
                        ProbedAt = snapshot.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                        Confidence = site.Confidence,
                        DaysRemaining = days,
                        Expired = expired
                    });
                }

                foreach (var cert in snapshot.Certificates)
                {
                    var (days, expired) = CertDays(cert);
                    Certificates.Add(new CertificateAssetRow
                    {
                        ServerId = snapshot.ServerId,
                        ServerName = snapshot.ServerName,
                        Domain = siteDomainsByPath.TryGetValue(cert.Path, out var domain) ? domain : string.Empty,
                        Path = cert.Path,
                        Subject = cert.Subject,
                        Domains = cert.Domains,
                        NotAfter = FormatDate(cert.NotAfter),
                        DaysText = days is null ? "—" : $"{days} 天",
                        Status = CertStatus(days, expired),
                        ProbedAt = snapshot.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                        DaysRemaining = days,
                        Expired = expired
                    });
                }
            }

            LastRefreshText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            StatusText = $"已汇总 {TotalServers} 台服务器、{TotalSites} 个站点、{TotalCertificates} 张证书。";
            RefreshCounts();
        }
        catch (Exception ex)
        {
            StatusText = "读取失败：" + ex.Message;
        }
    }

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(TotalSites));
        OnPropertyChanged(nameof(TotalCertificates));
        OnPropertyChanged(nameof(Expiring30));
        OnPropertyChanged(nameof(Expiring90));
        OnPropertyChanged(nameof(ExpiredCertificates));
        SitesView.Refresh();
        CertificatesView.Refresh();
    }

    private bool FilterSite(object item)
    {
        if (item is not SiteAssetRow row) return false;
        if (!MatchesExpiry(row.DaysRemaining, row.Expired)) return false;
        var text = SearchText.Trim();
        if (text.Length == 0) return true;
        return Contains(row.ServerName) || Contains(row.Domain) || Contains(row.Ports) ||
               Contains(row.Protocol) || Contains(row.ServerType) || Contains(row.SiteType) ||
               Contains(row.CertificatePath) || Contains(row.CertificateSubject) || Contains(row.Status);

        bool Contains(string value) => value.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private bool FilterCertificate(object item)
    {
        if (item is not CertificateAssetRow row) return false;
        if (!MatchesExpiry(row.DaysRemaining, row.Expired)) return false;
        var text = SearchText.Trim();
        if (text.Length == 0) return true;
        return Contains(row.ServerName) || Contains(row.Domain) || Contains(row.Path) ||
               Contains(row.Subject) || Contains(row.Domains) || Contains(row.Status);

        bool Contains(string value) => value.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesExpiry(int? days, bool expired) => ExpiryFilter switch
    {
        "已过期" => expired || days < 0,
        "30 天内" => days is >= 0 and <= 30,
        "90 天内" => days is >= 0 and <= 90,
        _ => true
    };

    private static (int? Days, bool Expired) CertDays(SiteProbeCertificate? cert)
    {
        if (cert is null)
            return (null, false);
        return int.TryParse(cert.DaysRemaining, out var days)
            ? (days, cert.Expired || days < 0)
            : (null, cert.Expired);
    }

    private static string FormatDate(string value) =>
        DateTime.TryParse(value, out var date) ? date.ToString("yyyy-MM-dd") : value;

    private static string CertStatus(int? days, bool expired)
    {
        if (expired || days < 0) return "已过期";
        if (days is null) return "未知";
        if (days <= 7) return "7 天内到期";
        if (days <= 30) return "30 天内到期";
        if (days <= 90) return "90 天内到期";
        return "正常";
    }

    private void ExportSites()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV 文件|*.csv",
            FileName = "site-assets.csv",
            AddExtension = true,
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("服务器,域名,端口,协议,服务,类型,配置来源,证书路径,证书主题,到期时间,剩余天数,状态,探测时间,置信度");
            foreach (var row in SitesView.Cast<SiteAssetRow>())
                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(row.ServerName), Csv(row.Domain), Csv(row.Ports), Csv(row.Protocol),
                    Csv(row.ServerType), Csv(row.SiteType), Csv(row.ConfigSource),
                    Csv(row.CertificatePath), Csv(row.CertificateSubject), Csv(row.NotAfter),
                    Csv(row.DaysText), Csv(row.Status), Csv(row.ProbedAt), Csv(row.Confidence)
                }));
            File.WriteAllText(dialog.FileName, sb.ToString());
            StatusText = "站点资产已导出：" + dialog.FileName;
        }
        catch (Exception ex)
        {
            _dialogs.Error("导出失败：" + ex.Message, "站点资产汇总");
        }
    }

    private void ExportCertificates()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV 文件|*.csv",
            FileName = "site-certificates.csv",
            AddExtension = true,
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("服务器,域名,证书路径,主题,域名/SAN,到期时间,剩余天数,状态,探测时间");
            foreach (var row in CertificatesView.Cast<CertificateAssetRow>())
                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(row.ServerName), Csv(row.Domain), Csv(row.Path), Csv(row.Subject),
                    Csv(row.Domains), Csv(row.NotAfter), Csv(row.DaysText), Csv(row.Status), Csv(row.ProbedAt)
                }));
            File.WriteAllText(dialog.FileName, sb.ToString());
            StatusText = "证书清单已导出：" + dialog.FileName;
        }
        catch (Exception ex)
        {
            _dialogs.Error("导出失败：" + ex.Message, "站点资产汇总");
        }
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(ResultDirectory);
        NativeShell.OpenFolder(ResultDirectory);
    }

    private static string Csv(string? value) =>
        "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
