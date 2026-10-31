using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Snapshot.Collectors;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public enum SiteDiscoveryStepState
{
    Pending,
    Running,
    Success,
    Skipped,
    NeedsElevation,
    Failed,
    Stopped
}

public sealed class SiteDiscoveryStep : INotifyPropertyChanged
{
    private SiteDiscoveryStepState _state;
    private string _detail = string.Empty;
    private string _durationText = string.Empty;

    public required string Name { get; init; }
    public required string Description { get; init; }

    public SiteDiscoveryStepState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(StateGlyph));
        }
    }

    public string Detail
    {
        get => _detail;
        set { if (_detail == value) return; _detail = value; OnPropertyChanged(); }
    }

    public string DurationText
    {
        get => _durationText;
        set { if (_durationText == value) return; _durationText = value; OnPropertyChanged(); }
    }

    public string StateText => State switch
    {
        SiteDiscoveryStepState.Pending => "等待",
        SiteDiscoveryStepState.Running => "进行中",
        SiteDiscoveryStepState.Success => "完成",
        SiteDiscoveryStepState.Skipped => "跳过",
        SiteDiscoveryStepState.NeedsElevation => "需要提权",
        SiteDiscoveryStepState.Failed => "失败",
        SiteDiscoveryStepState.Stopped => "已停止",
        _ => string.Empty
    };

    public string StateGlyph => State switch
    {
        SiteDiscoveryStepState.Pending => "○",
        SiteDiscoveryStepState.Running => "◌",
        SiteDiscoveryStepState.Success => "✓",
        SiteDiscoveryStepState.Skipped => "—",
        SiteDiscoveryStepState.NeedsElevation => "!",
        SiteDiscoveryStepState.Failed => "×",
        SiteDiscoveryStepState.Stopped => "■",
        _ => "○"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class SiteProbeSite
{
    public string Domain { get; set; } = string.Empty;
    public string Ports { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string ServerType { get; set; } = string.Empty;
    public string ServerVersion { get; set; } = string.Empty;
    public string ConfigSource { get; set; } = string.Empty;
    public string CertificatePath { get; set; } = string.Empty;
    public string CertificateSubject { get; set; } = string.Empty;
    public string CertificateExpiry { get; set; } = string.Empty;
    public string SiteType { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public string ProxyTarget { get; set; } = string.Empty;
    public string Upstream { get; set; } = string.Empty;
    public string Confidence { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
}

public sealed class SiteProbePort
{
    public int Port { get; init; }
    public string Bind { get; init; } = string.Empty;
    public string Process { get; init; } = string.Empty;
    public string Pid { get; init; } = string.Empty;
    public string HttpStatus { get; set; } = string.Empty;
    public string HttpsStatus { get; set; } = string.Empty;
    public string ServerHeader { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string TlsStatus { get; set; } = string.Empty;
    public string TlsSubject { get; set; } = string.Empty;
    public string TlsIssuer { get; set; } = string.Empty;
    public string TlsNotAfter { get; set; } = string.Empty;
    public string TlsSni { get; set; } = string.Empty;
    public string HttpTime { get; set; } = string.Empty;
    public string HttpsTime { get; set; } = string.Empty;
    public string HttpLocation { get; set; } = string.Empty;
    public string HttpsLocation { get; set; } = string.Empty;
    public string Hsts { get; set; } = string.Empty;
    public string HttpVersion { get; set; } = string.Empty;
    public string HttpsVersion { get; set; } = string.Empty;
    public string TlsHandshake { get; set; } = string.Empty;
}

public sealed class SiteProbeService
{
    public string Name { get; init; } = string.Empty;
    public string Image { get; init; } = string.Empty;
    public string Ports { get; init; } = string.Empty;
    public string ServiceType { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Evidence { get; init; } = string.Empty;
}

public sealed class SiteProbeCertificate
{
    public string Path { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Domains { get; init; } = string.Empty;
    public string NotAfter { get; init; } = string.Empty;
    public string DaysRemaining { get; init; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public bool Expired { get; init; }
}

public sealed class SiteProbeHistoryItem
{
    public required string FilePath { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required int SiteCount { get; init; }
    public required string Mode { get; init; }
    public string DisplayTime => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
    public string Summary => $"{SiteCount} 个站点 · {Mode}";
    public SiteProbeSnapshot? Snapshot { get; init; }
}

public sealed class SiteProbeDiffItem
{
    public required string Change { get; init; }
    public required string Domain { get; init; }
    public required string Detail { get; init; }
}

public sealed class SiteProbeSnapshot
{
    public string ServerId { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool Elevated { get; set; }
    public List<SiteProbeSite> Sites { get; set; } = new();
    public List<SiteProbePort> Ports { get; set; } = new();
    public List<SiteProbeCertificate> Certificates { get; set; } = new();
    public List<SiteProbeService> NonWebServices { get; set; } = new();
}

public sealed class SiteDiscoveryViewModel : INotifyPropertyChanged
{
    private readonly ISshService _ssh;
    private readonly IUserDialogService _dialogs;
    private readonly IAuditLogService? _audit;
    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _elevated;
    private string? _nginxPath;
    private string? _apachePath;
    private string _nginxVersion = string.Empty;
    private string _nginxServerType = "Nginx";
    private string _apacheVersion = string.Empty;
    private string _caddyVersion = string.Empty;
    private string _traefikVersion = string.Empty;
    private readonly List<SiteProbeSite> _extraSites = new();
    private readonly List<string> _extraCertPaths = new();
    private readonly List<NginxTlsSnapshotCollector.NginxSite> _nginxSites = new();
    private readonly Dictionary<string, string> _nginxUpstreams = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ApacheSite> _apacheSites = new();
    private readonly List<string> _apacheCertPaths = new();
    private readonly Dictionary<string, List<string>> _apacheCertByFile = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _apacheRootsByFile = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _apacheProxyByFile = new(StringComparer.Ordinal);
    private readonly List<SiteProbeCertificate> _certificateCache = new();

    private const string PathPrefix = "export PATH=/usr/sbin:/sbin:/usr/bin:/bin:$PATH; ";

    public SiteDiscoveryViewModel(SshServerConfig server, ISshService ssh,
        IUserDialogService? dialogs = null, IAuditLogService? audit = null)
    {
        Server = server;
        _ssh = ssh;
        _dialogs = dialogs ?? new UserDialogService();
        _audit = audit;
        StartDirectCommand = new RelayCommand(_ => _ = StartAsync(false), _ => !IsRunning);
        StartElevatedCommand = new RelayCommand(_ => _ = StartAsync(true), _ => !IsRunning && Server.SudoType != SudoType.None);
        StopCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsRunning);
        SaveCommand = new RelayCommand(_ => Save(), _ => CanSave);
        OpenFolderCommand = new RelayCommand(_ => OpenFolder());
        RefreshHistoryCommand = new RelayCommand(_ => LoadHistory());
        CompareHistoryCommand = new RelayCommand(_ => CompareHistory(), _ => SelectedHistory is not null);
        DeleteHistoryCommand = new RelayCommand(_ => DeleteHistory(), _ => SelectedHistory is not null);
        ExportHistoryCommand = new RelayCommand(_ => ExportHistory());
        CopyDiffCommand = new RelayCommand(_ => CopyDiff(), _ => Diff.Count > 0);
        RetryStepCommand = new RelayCommand(_ => _ = RetrySelectedStepAsync(), _ => CanRetrySelectedStep);
        Steps.Add(new SiteDiscoveryStep { Name = "连接与系统信息", Description = "系统版本、内核、当前用户" });
        Steps.Add(new SiteDiscoveryStep { Name = "Web 服务识别", Description = "Nginx / Apache / OpenResty 进程、路径与版本" });
        Steps.Add(new SiteDiscoveryStep { Name = "Nginx 配置", Description = "解析 server_name、listen、SSL 证书" });
        Steps.Add(new SiteDiscoveryStep { Name = "Apache 配置", Description = "解析 VirtualHost、监听端口、SSL 证书" });
        Steps.Add(new SiteDiscoveryStep { Name = "扩展 Web 服务", Description = "Docker / Caddy / Traefik 站点证据" });
        Steps.Add(new SiteDiscoveryStep { Name = "证书信息", Description = "读取证书主题、SAN、有效期" });
        Steps.Add(new SiteDiscoveryStep { Name = "监听端口探测", Description = "本机 loopback HTTP/HTTPS 探测" });
        Steps.Add(new SiteDiscoveryStep { Name = "汇总去重", Description = "合并配置与端口证据" });
        LoadHistory();
    }

    public SshServerConfig Server { get; }
    public string Title => $"HTTP站点探测 - {Server.Name}";
    public string Subtitle => $"{Server.Host}:{Server.Port}";
    public ObservableCollection<SiteDiscoveryStep> Steps { get; } = new();
    public ObservableCollection<SiteProbeSite> Sites { get; } = new();
    public ObservableCollection<SiteProbePort> Ports { get; } = new();
    public ObservableCollection<SiteProbeCertificate> Certificates { get; } = new();
    public ObservableCollection<SiteProbeService> NonWebServices { get; } = new();
    public ObservableCollection<SiteProbeHistoryItem> History { get; } = new();
    public ObservableCollection<SiteProbeDiffItem> Diff { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    public ICommand StartDirectCommand { get; }
    public ICommand StartElevatedCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand RefreshHistoryCommand { get; }
    public ICommand CompareHistoryCommand { get; }
    public ICommand DeleteHistoryCommand { get; }
    public ICommand ExportHistoryCommand { get; }
    public ICommand CopyDiffCommand { get; }
    public ICommand RetryStepCommand { get; }

    private SiteDiscoveryStep? _selectedStep;
    public SiteDiscoveryStep? SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (ReferenceEquals(_selectedStep, value)) return;
            _selectedStep = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRetrySelectedStep));
            (RetryStepCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool CanRetrySelectedStep => !IsRunning &&
        SelectedStep?.State is SiteDiscoveryStepState.NeedsElevation
            or SiteDiscoveryStepState.Failed
            or SiteDiscoveryStepState.Stopped;

    private SiteProbeHistoryItem? _selectedHistory;
    public SiteProbeHistoryItem? SelectedHistory
    {
        get => _selectedHistory;
        set
        {
            if (ReferenceEquals(_selectedHistory, value)) return;
            _selectedHistory = value;
            OnPropertyChanged();
            (CompareHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsRunning
    {
        get => _running;
        private set
        {
            if (_running == value) return;
            _running = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(StatusText));
            (StartDirectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StartElevatedCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanRetrySelectedStep));
            (RetryStepCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsElevated => _elevated;
    public string ModeText => _elevated ? "提权探测" : "直接探测";
    public bool CanSave => !IsRunning && Sites.Count > 0;
    public int ProgressMaximum => Steps.Count == 0 ? 1 : Steps.Count;
    public int ProgressValue => Steps.Count(step => step.State is not (SiteDiscoveryStepState.Pending or SiteDiscoveryStepState.Running));
    public double ProgressPercent => ProgressMaximum == 0 ? 0 : (double)ProgressValue / ProgressMaximum * 100;

    private string _statusText = "选择探测模式后开始。";
    public string StatusText
    {
        get => _statusText;
        private set { if (_statusText == value) return; _statusText = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusTextWithMode)); }
    }
    public string StatusTextWithMode => $"{ModeText} · {StatusText}";

    private string _logText = string.Empty;
    public string LogText
    {
        get => _logText;
        private set { if (_logText == value) return; _logText = value; OnPropertyChanged(); }
    }

    private string _savedPath = string.Empty;
    public string SavedPath
    {
        get => _savedPath;
        private set { if (_savedPath == value) return; _savedPath = value; OnPropertyChanged(); }
    }

    private async Task RetrySelectedStepAsync()
    {
        if (IsRunning || SelectedStep is not { } step)
            return;
        var index = Steps.IndexOf(step);
        if (index < 0)
            return;
        if (Server.SudoType == SudoType.None)
        {
            _dialogs.Warn("该服务器未配置 sudo/Su 提权，无法重试。", "HTTP站点探测");
            return;
        }

        _elevated = true;
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(StatusTextWithMode));
        _cts = new CancellationTokenSource();
        IsRunning = true;
        step.State = SiteDiscoveryStepState.Pending;
        step.Detail = string.Empty;
        StatusText = $"提权重试「{step.Name}」…";
        try
        {
            await ExecuteStepAsync(step, StepAction(index));
            if (index <= 6 && step.State == SiteDiscoveryStepState.Success)
                await ExecuteStepAsync(Steps[7], SummaryStepAsync);
            StatusText = step.State == SiteDiscoveryStepState.Success
                ? $"「{step.Name}」重试完成。"
                : $"「{step.Name}」重试未完成：{step.StateText}";
        }
        catch (OperationCanceledException)
        {
            step.State = SiteDiscoveryStepState.Stopped;
            StatusText = "重试已停止。";
        }
        catch (Exception ex)
        {
            step.State = SiteDiscoveryStepState.Failed;
            step.Detail = ex.Message;
            StatusText = "重试失败: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            UpdateProgress();
            await AuditAsync();
        }
    }

    private Func<SiteDiscoveryStep, Task> StepAction(int index) => index switch
    {
        0 => SystemStepAsync,
        1 => WebProcessStepAsync,
        2 => NginxStepAsync,
        3 => ApacheStepAsync,
        4 => ExtendedWebStepAsync,
        5 => CertificateStepAsync,
        6 => PortStepAsync,
        _ => SummaryStepAsync
    };

    public async Task<bool> RunForBatchAsync(bool elevated)
    {
        if (elevated && Server.SudoType == SudoType.None)
            elevated = false;
        await StartAsync(elevated);
        if (Sites.Count == 0)
            return false;
        Save();
        return true;
    }

    public void StopBatchProbe() => _cts?.Cancel();

    private async Task StartAsync(bool elevated)
    {
        if (IsRunning) return;
        if (elevated && Server.SudoType == SudoType.None)
        {
            _dialogs.Warn("该服务器未配置 sudo/Su 提权，将按直接探测执行。", "站点探测");
            elevated = false;
        }

        _elevated = elevated;
        _cts = new CancellationTokenSource();
        IsRunning = true;
        SavedPath = string.Empty;
        Sites.Clear();
        Ports.Clear();
        Certificates.Clear();
        NonWebServices.Clear();
        _nginxSites.Clear();
        _nginxUpstreams.Clear();
        _extraSites.Clear();
        _extraCertPaths.Clear();
        _apacheSites.Clear();
        _apacheCertPaths.Clear();
        _apacheCertByFile.Clear();
        _apacheRootsByFile.Clear();
        _apacheProxyByFile.Clear();
        _certificateCache.Clear();
        _nginxPath = null;
        _apachePath = null;
        _nginxVersion = string.Empty;
        _nginxServerType = "Nginx";
        _apacheVersion = string.Empty;
        _caddyVersion = string.Empty;
        _traefikVersion = string.Empty;
        LogLines.Clear();
        LogText = string.Empty;
        foreach (var step in Steps)
        {
            step.State = SiteDiscoveryStepState.Pending;
            step.Detail = string.Empty;
            step.DurationText = string.Empty;
        }
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(StatusTextWithMode));
        UpdateProgress();
        StatusText = "正在连接服务器…";

        try
        {
            await ExecuteStepAsync(Steps[0], SystemStepAsync);
            await ExecuteStepAsync(Steps[1], WebProcessStepAsync);
            await ExecuteStepAsync(Steps[2], NginxStepAsync);
            await ExecuteStepAsync(Steps[3], ApacheStepAsync);
            await ExecuteStepAsync(Steps[4], ExtendedWebStepAsync);
            await ExecuteStepAsync(Steps[5], CertificateStepAsync);
            await ExecuteStepAsync(Steps[6], PortStepAsync);
            await ExecuteStepAsync(Steps[7], SummaryStepAsync);
            StatusText = Sites.Count == 0
                ? "探测完成，未发现可识别的站点。"
                : $"探测完成，发现 {Sites.Count} 个站点 / {Ports.Count} 个监听端口。";
        }
        catch (OperationCanceledException)
        {
            foreach (var step in Steps.Where(s => s.State is SiteDiscoveryStepState.Pending or SiteDiscoveryStepState.Running))
                step.State = SiteDiscoveryStepState.Stopped;
            StatusText = "探测已停止。";
        }
        catch (Exception ex)
        {
            StatusText = "探测失败: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            UpdateProgress();
            await AuditAsync();
        }
    }

    private async Task ExecuteStepAsync(SiteDiscoveryStep step, Func<SiteDiscoveryStep, Task> action)
    {
        _cts?.Token.ThrowIfCancellationRequested();
        step.State = SiteDiscoveryStepState.Running;
        step.Detail = string.Empty;
        StatusText = $"正在{step.Name}…";
        var started = System.Diagnostics.Stopwatch.StartNew();
        UpdateProgress();
        try
        {
            await action(step);
            if (step.State == SiteDiscoveryStepState.Running)
                step.State = SiteDiscoveryStepState.Success;
        }
        catch (OperationCanceledException)
        {
            step.State = SiteDiscoveryStepState.Stopped;
            throw;
        }
        catch (Exception ex)
        {
            step.State = SiteDiscoveryStepState.Failed;
            step.Detail = ex.Message;
        }
        finally
        {
            started.Stop();
            step.DurationText = $"{started.Elapsed.TotalSeconds:0.0}s";
            UpdateProgress();
            if (step.State is SiteDiscoveryStepState.Success or SiteDiscoveryStepState.Skipped)
                StatusText = $"{step.Name}：{step.StateText}" +
                             (string.IsNullOrWhiteSpace(step.Detail) ? string.Empty : " · " + step.Detail);
            else if (step.State == SiteDiscoveryStepState.NeedsElevation)
                StatusText = $"{step.Name}需要提权";
        }
    }

    private async Task SystemStepAsync(SiteDiscoveryStep step)
    {
        var result = await RunAsync(
            "cat /etc/os-release 2>/dev/null | head -20; echo '##UNAME'; uname -r 2>/dev/null; " +
            "echo '##USER'; id -un 2>/dev/null; echo '##ROOT'; id -u 2>/dev/null; " +
            "echo '##SUDO'; command -v sudo 2>/dev/null; command -v su 2>/dev/null; true",
            step, 20);
        if (result is null) return;
        var os = Regex.Match(result.Output, @"^PRETTY_NAME=""?([^""\r\n]+)", RegexOptions.Multiline);
        step.Detail = os.Success ? os.Groups[1].Value.Trim() : "系统信息已读取";
        AddLog("系统", step.Detail);
    }

    private async Task WebProcessStepAsync(SiteDiscoveryStep step)
    {
        var command =
            "NGX=''; for p in $(pgrep -x nginx 2>/dev/null); do e=$(readlink /proc/$p/exe 2>/dev/null); " +
            "case \"$e\" in */nginx) NGX=\"$e\"; break;; esac; done; " +
            "[ -z \"$NGX\" ] && NGX=$(command -v nginx 2>/dev/null); " +
            "if [ -n \"$NGX\" ]; then echo \"##NGINX_BIN $NGX\"; \"$NGX\" -v 2>&1; fi; " +
            "AP=''; for p in $(pgrep -x apache2 2>/dev/null) $(pgrep -x httpd 2>/dev/null); do e=$(readlink /proc/$p/exe 2>/dev/null); " +
            "case \"$e\" in */apache2|*/httpd) AP=\"$e\"; break;; esac; done; " +
            "[ -z \"$AP\" ] && AP=$(command -v apache2 2>/dev/null || command -v httpd 2>/dev/null); " +
            "if [ -n \"$AP\" ]; then echo \"##APACHE_BIN $AP\"; \"$AP\" -v 2>&1; fi; true";
        var result = await RunAsync(command, step, 20);
        if (result is null) return;

        var nginxBin = Regex.Match(result.Output, @"^##NGINX_BIN\s+(.+)$", RegexOptions.Multiline);
        if (nginxBin.Success)
        {
            _nginxPath = nginxBin.Groups[1].Value.Trim();
            _nginxVersion = Regex.Match(result.Output, @"nginx version:\s*([^\r\n]+)", RegexOptions.IgnoreCase)
                .Groups[1].Value.Trim();
            if (_nginxVersion.Contains("openresty", StringComparison.OrdinalIgnoreCase) ||
                _nginxPath.Contains("openresty", StringComparison.OrdinalIgnoreCase))
                _nginxServerType = "OpenResty";
        }
        var apacheBin = Regex.Match(result.Output, @"^##APACHE_BIN\s+(.+)$", RegexOptions.Multiline);
        if (apacheBin.Success)
        {
            _apachePath = apacheBin.Groups[1].Value.Trim();
            _apacheVersion = Regex.Match(result.Output, @"(?:Apache|httpd)/[^\r\n]+", RegexOptions.IgnoreCase)
                .Value.Trim();
        }

        var found = new List<string>();
        if (_nginxPath is not null) found.Add("Nginx " + _nginxVersion);
        if (_apachePath is not null) found.Add("Apache " + _apacheVersion);
        if (found.Count == 0)
        {
            step.State = SiteDiscoveryStepState.Skipped;
            step.Detail = "未检测到 Nginx / Apache 进程";
            return;
        }
        step.Detail = string.Join("；", found);
        AddLog("Web 服务", step.Detail);
    }

    private async Task NginxStepAsync(SiteDiscoveryStep step)
    {
        _nginxSites.Clear();
        _nginxUpstreams.Clear();
        if (_nginxPath is null)
        {
            step.State = SiteDiscoveryStepState.Skipped;
            step.Detail = "未检测到 Nginx，跳过";
            return;
        }
        var command = $"{ShellQuote.Single(_nginxPath)} -T 2>&1 | head -3000";
        var result = await RunAsync(command, step, 60);
        if (result is null) return;
        var text = NginxTlsSnapshotCollector.SanitizeNginxConfig(result.Output);
        if (text.Trim() == "##nginx_missing")
        {
            step.State = SiteDiscoveryStepState.Skipped;
            step.Detail = "nginx -T 不可用";
            return;
        }
        var (sites, truncated) = NginxTlsSnapshotCollector.ParseNginxConfig(text);
        _nginxSites.AddRange(sites);
        ParseNginxUpstreams(text);
        step.Detail = $"解析 {sites.Count} 个 server 块" + (truncated ? "（已截断）" : string.Empty);
        AddLog("Nginx", step.Detail);
    }

    private async Task ApacheStepAsync(SiteDiscoveryStep step)
    {
        _apacheSites.Clear();
        _apacheCertPaths.Clear();
        _apacheCertByFile.Clear();
        _apacheRootsByFile.Clear();
        _apacheProxyByFile.Clear();
        if (_apachePath is null)
        {
            step.State = SiteDiscoveryStepState.Skipped;
            step.Detail = "未检测到 Apache，跳过";
            return;
        }
        var command =
            "echo '##APACHE_S'; " + ShellQuote.Single(_apachePath) + " -S 2>&1 | head -1500; " +
            "echo '##APACHE_GREP'; grep -RHE '^[[:space:]]*(SSLCertificateFile|SSLCertificateKeyFile|DocumentRoot|ProxyPass)[[:space:]]' " +
            "/etc/apache2 /etc/httpd /www/server/apache/conf /usr/local/apache2/conf 2>/dev/null | head -800; true";
        var result = await RunAsync(command, step, 60);
        if (result is null) return;

        var sections = result.Output.Split("##APACHE_GREP", StringSplitOptions.None);
        var sOutput = sections[0];
        if (sections.Length > 1)
        {
            foreach (var line in sections[1].Replace("\r\n", "\n").Split('\n'))
            {
                var location = Regex.Match(line, @"^(?<file>/.+?):(?<line>\d+):(?<rest>.*)$");
                var rest = location.Success ? location.Groups["rest"].Value : line;
                var file = location.Success ? NormalizeConfigPath(location.Groups["file"].Value) : string.Empty;
                var cert = Regex.Match(rest, @"SSLCertificateFile\s+(.+)$", RegexOptions.IgnoreCase);
                if (cert.Success)
                {
                    var path = cert.Groups[1].Value.Trim().Trim('"', '\'');
                    if (path.Length == 0) continue;
                    _apacheCertPaths.Add(path);
                    if (file.Length > 0)
                        AddFileValue(_apacheCertByFile, file, path);
                    continue;
                }
                var root = Regex.Match(rest, @"DocumentRoot\s+(.+)$", RegexOptions.IgnoreCase);
                if (root.Success && file.Length > 0)
                {
                    AddFileValue(_apacheRootsByFile, file, root.Groups[1].Value.Trim().Trim('"', '\''));
                    continue;
                }
                var proxy = Regex.Match(rest, @"ProxyPass\s+(.+)$", RegexOptions.IgnoreCase);
                if (proxy.Success && file.Length > 0)
                    AddFileValue(_apacheProxyByFile, file, proxy.Groups[1].Value.Trim().Trim('"', '\''));
            }
        }

        ParseApacheSites(sOutput);
        step.Detail = $"解析 {_apacheSites.Count} 个 VirtualHost" +
                     (_apacheCertPaths.Count > 0 ? $"，{_apacheCertPaths.Distinct().Count()} 个证书路径" : string.Empty);
        AddLog("Apache", step.Detail);
    }

    private async Task ExtendedWebStepAsync(SiteDiscoveryStep step)
    {
        _extraSites.Clear();
        _extraCertPaths.Clear();
        NonWebServices.Clear();
        _caddyVersion = string.Empty;
        _traefikVersion = string.Empty;

        var command =
            "if command -v docker >/dev/null 2>&1; then echo '##DOCKER'; " +
            "docker ps --format '{{.Names}}\\t{{.Image}}\\t{{.Ports}}\\t{{.Labels}}' 2>&1 | head -200; fi; " +
            "if command -v caddy >/dev/null 2>&1; then echo '##CADDY'; caddy version 2>&1; " +
            "for f in /etc/caddy/Caddyfile /opt/caddy/Caddyfile /usr/local/etc/caddy/Caddyfile; do " +
            "if [ -f \"$f\" ]; then echo \"##CADDYFILE $f\"; cat \"$f\" 2>&1 | head -1000; fi; done; fi; " +
            "if command -v traefik >/dev/null 2>&1; then echo '##TRAEFIK'; traefik version 2>&1; fi; true";
        var result = await RunAsync(command, step, 60);
        if (result is null) return;

        var dockerSection = ExtractSection(result.Output, "##DOCKER");
        var caddySection = ExtractSection(result.Output, "##CADDY");
        var caddyFileStart = result.Output.IndexOf("##CADDYFILE", StringComparison.Ordinal);
        var caddyFileSection = caddyFileStart < 0 ? string.Empty : result.Output[caddyFileStart..];
        var traefikSection = ExtractSection(result.Output, "##TRAEFIK");

        if (!string.IsNullOrWhiteSpace(caddySection))
        {
            var versionLine = caddySection.Replace("\r\n", "\n").Split('\n')
                .Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("v", StringComparison.Ordinal));
            _caddyVersion = versionLine ?? string.Empty;
        }
        if (!string.IsNullOrWhiteSpace(traefikSection))
        {
            _traefikVersion = traefikSection.Replace("\r\n", "\n").Split('\n')
                .Select(x => x.Trim()).FirstOrDefault(x => x.Contains("Version", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        }

        ParseDockerSites(dockerSection);
        ParseCaddySites(caddyFileSection);
        var found = new List<string>();
        if (_extraSites.Count > 0) found.Add($"{_extraSites.Count} 个扩展站点");
        if (_caddyVersion.Length > 0) found.Add("Caddy " + _caddyVersion);
        if (_traefikVersion.Length > 0) found.Add("Traefik " + _traefikVersion);
        if (found.Count == 0)
        {
            step.State = SiteDiscoveryStepState.Skipped;
            step.Detail = "未检测到 Docker / Caddy / Traefik 站点";
            return;
        }
        step.Detail = string.Join("；", found);
        AddLog("扩展 Web", step.Detail);
    }

    private static string ExtractSection(string output, string marker)
    {
        var text = output.Replace("\r\n", "\n");
        var start = text.IndexOf(marker + "\n", StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        start += marker.Length + 1;
        var next = text.IndexOf("\n##", start, StringComparison.Ordinal);
        return (next < 0 ? text[start..] : text[start..next]).Trim();
    }

    private void ParseDockerSites(string section)
    {
        if (string.IsNullOrWhiteSpace(section)) return;
        foreach (var raw in section.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            var name = parts[0].Trim('/');
            var image = parts[1].Trim();
            var portsText = parts[2];
            var labels = parts.Length > 3 ? parts[3] : string.Empty;
            var hostPorts = Regex.Matches(portsText, @"(?:^|,|\s)(?:[0-9a-fA-F:.\[\]]+:)?(\d+)->")
                .Select(match => match.Groups[1].Value)
                .Select(value => int.TryParse(value, out var port) ? port : 0)
                .Where(port => port > 0)
                .Distinct()
                .ToList();
            var hasTraefikLabels = labels.Contains("traefik", StringComparison.OrdinalIgnoreCase);
            if (IsKnownNonWebImage(image) && !hasTraefikLabels && !hostPorts.Any(port => port is 80 or 443))
            {
                NonWebServices.Add(new SiteProbeService
                {
                    Name = name,
                    Image = image,
                    Ports = portsText,
                    ServiceType = ClassifyContainerService(image),
                    Status = "运行中",
                    Evidence = "docker ps"
                });
                continue;
            }
            if (hostPorts.Count == 0) continue;

            var webPort = hostPorts.Any(port => port is 80 or 443 or 3000 or 5000 or 8000 or 8080 or 8088 or 8443 or 9000);
            if (!hasTraefikLabels && !webPort && !IsLikelyWebImage(image))
                continue;

            var hosts = Regex.Matches(labels, @"Host\(([^)]+)\)")
                .Select(match => match.Groups[1].Value.Trim().Trim('"', '\'', '`'))
                .Where(host => host.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (hosts.Count == 0) hosts.Add(name);

            foreach (var host in hosts)
            {
                foreach (var port in hostPorts)
                {
                    var row = BuildSite(host, new[] { port }, port is 443 or 8443,
                        labels.Contains("traefik", StringComparison.OrdinalIgnoreCase) ? "Traefik/Docker" : "Docker",
                        image, "docker ps", null);
                    row.SiteType = labels.Contains("traefik", StringComparison.OrdinalIgnoreCase) ? "Traefik" : "Docker";
                    row.ProxyTarget = image;
                    row.Evidence = "docker ps" + (labels.Length > 0 ? " / labels" : string.Empty);
                    _extraSites.Add(row);
                }
            }
        }
    }

    private void ParseCaddySites(string section)
    {
        if (string.IsNullOrWhiteSpace(section)) return;
        string? currentFile = null;
        var lines = section.Replace("\r\n", "\n").Split('\n');
        foreach (var raw in lines)
        {
            if (raw.StartsWith("##CADDYFILE ", StringComparison.Ordinal))
            {
                currentFile = raw["##CADDYFILE ".Length..].Trim();
                continue;
            }
            if (string.IsNullOrWhiteSpace(currentFile)) continue;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("tls ", StringComparison.Ordinal))
            {
                var tokens = line["tls".Length..].Trim().TrimEnd(';').Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length > 0)
                {
                    var cert = tokens[0].Trim('"', '\'');
                    if (IsSafeCertPath(cert)) _extraCertPaths.Add(cert);
                }
                continue;
            }
            if (raw.Length == 0 || char.IsWhiteSpace(raw[0]) || !line.EndsWith('{'))
                continue;
            var addressText = line.TrimEnd('{').Trim();
            if (addressText.Length == 0 || addressText == "{" || addressText.StartsWith("(", StringComparison.Ordinal))
                continue;
            foreach (var address in addressText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                AddCaddySite(address, currentFile);
        }
    }

    private void AddCaddySite(string address, string configPath)
    {
        var value = address.Trim().Trim(',');
        if (value.Length == 0) return;
        var scheme = value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "http" : "https";
        value = Regex.Replace(value, @"^https?://", string.Empty, RegexOptions.IgnoreCase);
        value = value.Split('/')[0];
        var host = value;
        var port = scheme == "http" ? 80 : 443;
        if (value.StartsWith(':'))
        {
            host = "localhost";
            if (int.TryParse(value[1..], out var onlyPort)) port = onlyPort;
        }
        else
        {
            var colon = value.LastIndexOf(':');
            if (colon > 0 && int.TryParse(value[(colon + 1)..], out var parsedPort))
            {
                host = value[..colon];
                port = parsedPort;
            }
        }
        if (host.Length == 0) host = "localhost";
        var row = BuildSite(host.TrimStart('*', '.'), new[] { port }, scheme == "https", "Caddy", _caddyVersion, configPath, null);
        row.SiteType = "Caddy";
        row.Evidence = "Caddyfile";
        _extraSites.Add(row);
    }

    private async Task CertificateStepAsync(SiteDiscoveryStep step)
    {
        Certificates.Clear();
        _certificateCache.Clear();
        var paths = _nginxSites.SelectMany(s => s.CertPaths)
            .Concat(_apacheCertPaths)
            .Concat(_extraCertPaths)
            .Where(IsSafeCertPath)
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToList();
        if (paths.Count == 0)
        {
            step.State = SiteDiscoveryStepState.Skipped;
            step.Detail = "未在配置中发现证书路径";
            return;
        }

        var command = string.Join("; ", paths.Select(path =>
            $"echo {ShellQuote.Single("##CERT:" + path)}; " +
            $"openssl x509 -in {ShellQuote.Single(path)} -noout -subject -enddate -ext subjectAltName -fingerprint -sha256 2>&1 | head -40"));
        var result = await RunAsync(command, step, 60);
        if (result is null) return;
        var parsed = NginxTlsSnapshotCollector.ParseCertificateBlocks(result.Output, DateTime.UtcNow, out var errors);
        foreach (var item in parsed)
        {
            var cert = new SiteProbeCertificate
            {
                Path = item.GetValueOrDefault("path") as string ?? string.Empty,
                Subject = item.GetValueOrDefault("subjectCn") as string ?? string.Empty,
                NotAfter = item.GetValueOrDefault("notAfter") as string ?? string.Empty,
                DaysRemaining = item.GetValueOrDefault("daysRemaining")?.ToString() ?? string.Empty,
                Fingerprint = item.GetValueOrDefault("fingerprint") as string ?? string.Empty,
                Domains = string.Join(", ", item.GetValueOrDefault("dnsNames") as List<string> ?? new List<string>()),
                Expired = item.GetValueOrDefault("expired") is true
            };
            _certificateCache.Add(cert);
            Certificates.Add(cert);
        }
        step.Detail = $"读取 {Certificates.Count} 张证书" + (errors > 0 ? $"，{errors} 张失败" : string.Empty);
    }

    private async Task PortStepAsync(SiteDiscoveryStep step)
    {
        Ports.Clear();
        var ss = await RunAsync("ss -H -tlnp 2>/dev/null | head -400", step, 30);
        if (ss is null) return;
        var listeners = PortMapSnapshotCollector.ParseSsListeners(ss.Output, "tcp");
        var common = new HashSet<int> { 80, 443, 3000, 5000, 8000, 8080, 8088, 8443, 9000, 9080, 9443 };
        var candidates = listeners
            .Where(row => row.GetValueOrDefault("port") is int)
            .Select(row => new PortCandidate(
                Convert.ToInt32(row["port"]),
                row.GetValueOrDefault("bind") as string ?? string.Empty,
                row.GetValueOrDefault("process") as string ?? string.Empty,
                row.GetValueOrDefault("pid")?.ToString() ?? string.Empty))
            .Where(c => c.Port > 0)
            .GroupBy(c => c.Port)
            .Select(g => g.First())
            .OrderBy(c => IsWebProcess(c.Process) ? 0 : common.Contains(c.Port) ? 1 : 2)
            .ThenBy(c => c.Port)
            .Take(30)
            .ToList();

        if (candidates.Count == 0)
        {
            step.State = SiteDiscoveryStepState.Skipped;
            step.Detail = "没有可探测的 TCP 监听端口";
            return;
        }

        var ports = candidates.Select(c => c.Port).Distinct().ToList();
        var hostMap = BuildHostMap();
        var builder = new StringBuilder();
        builder.Append("if ! command -v curl >/dev/null 2>&1; then echo '##CURL_MISSING'; exit 0; fi; ");
        foreach (var port in ports)
        {
            var host = hostMap.TryGetValue(port, out var value) ? value : "127.0.0.1";
            var hostHeader = ShellQuote.Single("Host: " + host);
            builder.Append($"echo '##PORT:{port}'; ");
            builder.Append($"curl -k -sS -I --noproxy '*' --connect-timeout 1 --max-time 2 -H {hostHeader} " +
                           $"-w '\\n##META_HTTP time=%{{time_total}} code=%{{http_code}} version=%{{http_version}} redirect=%{{redirect_url}}' " +
                           $"http://127.0.0.1:{port}/ 2>&1 | head -40; ");
            builder.Append($"echo '##HTTPS'; curl -k -sS -I --noproxy '*' --connect-timeout 1 --max-time 2 -H {hostHeader} " +
                           $"-w '\\n##META_HTTPS time=%{{time_total}} code=%{{http_code}} version=%{{http_version}} redirect=%{{redirect_url}}' " +
                           $"https://127.0.0.1:{port}/ 2>&1 | head -40; ");
        }
        var result = await RunAsync(builder.ToString(), step, 120);
        if (result is null) return;
        if (result.Output.Contains("##CURL_MISSING", StringComparison.Ordinal))
        {
            step.State = SiteDiscoveryStepState.Skipped;
            step.Detail = "服务器未安装 curl，跳过 HTTP 探测";
            return;
        }

        foreach (var candidate in candidates)
        {
            var item = new SiteProbePort
            {
                Port = candidate.Port,
                Bind = candidate.Bind,
                Process = candidate.Process,
                Pid = candidate.Pid
            };
            var block = ExtractPortBlock(result.Output, candidate.Port);
            if (block is not null)
            {
                var parts = block.Split("##HTTPS", StringSplitOptions.None);
                item.HttpStatus = FindStatusLine(parts[0]);
                item.HttpsStatus = parts.Length > 1 ? FindStatusLine(parts[1]) : string.Empty;
                item.ServerHeader = FindHeader(parts[0], "Server") ?? FindHeader(parts.Length > 1 ? parts[1] : string.Empty, "Server") ?? string.Empty;
                item.HttpTime = FindMetaValue(parts[0], "##META_HTTP", "time");
                item.HttpsTime = parts.Length > 1 ? FindMetaValue(parts[1], "##META_HTTPS", "time") : string.Empty;
                item.HttpVersion = FindMetaValue(parts[0], "##META_HTTP", "version");
                item.HttpsVersion = parts.Length > 1 ? FindMetaValue(parts[1], "##META_HTTPS", "version") : string.Empty;
                item.HttpLocation = FindHeader(parts[0], "Location") ?? string.Empty;
                item.HttpsLocation = parts.Length > 1 ? FindHeader(parts[1], "Location") ?? string.Empty : string.Empty;
                item.Hsts = FindHeader(parts.Length > 1 ? parts[1] : parts[0], "Strict-Transport-Security") ?? string.Empty;
                item.Target = item.HttpsStatus.Length > 0 ? "https" : item.HttpStatus.Length > 0 ? "http" : string.Empty;
            }
            Ports.Add(item);
        }

        var tlsPorts = Ports.Where(p => p.Target == "https" || p.Port is 443 or 8443)
            .Select(p => p.Port).Distinct().ToList();
        if (tlsPorts.Count > 0)
        {
            var tlsCommand = new StringBuilder(
                "if ! command -v openssl >/dev/null 2>&1; then echo '##OPENSSL_MISSING'; exit 0; fi; ");
            foreach (var port in tlsPorts)
            {
                var host = hostMap.TryGetValue(port, out var value) ? value : "127.0.0.1";
                if (host.Contains('*')) host = "localhost";
                var quotedHost = ShellQuote.Single(host);
                tlsCommand.Append($"echo '##TLS:{port}'; ");
                tlsCommand.Append($"timeout 6 openssl s_client -connect 127.0.0.1:{port} -servername {quotedHost} </dev/null 2>&1 | " +
                                  "openssl x509 -noout -subject -issuer -enddate -ext subjectAltName -fingerprint -sha256 2>&1 | head -40; ");
                tlsCommand.Append($"echo '##TLS_VERIFY'; timeout 6 openssl s_client -connect 127.0.0.1:{port} -servername {quotedHost} -verify_hostname {quotedHost} </dev/null 2>&1 | " +
                                  "grep -E 'Verify return code|verify error|Verification' | head -5; ");
            }
            var tlsResult = await RunAsync(tlsCommand.ToString(), step, 90);
            if (tlsResult is not null && !tlsResult.Output.Contains("##OPENSSL_MISSING", StringComparison.Ordinal))
            {
                foreach (var item in Ports.Where(p => tlsPorts.Contains(p.Port)))
                {
                    var block = ExtractTlsBlock(tlsResult.Output, item.Port);
                    if (block is null) continue;
                    var parts = block.Split("##TLS_VERIFY", StringSplitOptions.None);
                    item.TlsSubject = FindOpenSslField(parts[0], "subject");
                    item.TlsIssuer = FindOpenSslField(parts[0], "issuer");
                    item.TlsNotAfter = FindOpenSslField(parts[0], "notAfter");
                    item.TlsSni = hostMap.TryGetValue(item.Port, out var sni) ? sni : "127.0.0.1";
                    item.TlsStatus = parts.Length > 1
                        ? FindTlsVerify(parts[1])
                        : "已连接";
                    item.TlsHandshake = item.TlsSubject.Length == 0
                        ? "握手失败/未读取证书"
                        : item.TlsStatus.Contains("code: 0", StringComparison.OrdinalIgnoreCase)
                            ? "握手成功（校验通过）"
                            : "握手成功（证书校验未通过）";
                }
            }
        }

        foreach (var item in Ports)
        {
            if (item.HttpStatus.Length > 0)
                item.Target = "http";
            else if (item.HttpsStatus.Length > 0 && item.TlsSubject.Length > 0)
                item.Target = "https";
            else
                item.Target = string.Empty;
        }

        step.Detail = $"探测 {Ports.Count} 个端口，{Ports.Count(p => p.Target.Length > 0)} 个返回 HTTP" +
                      $"；TLS 验证 {Ports.Count(p => p.TlsStatus.Length > 0)} 个";
        AddLog("端口", step.Detail);
    }

    private async Task SummaryStepAsync(SiteDiscoveryStep step)
    {
        var sites = new Dictionary<string, SiteProbeSite>(StringComparer.OrdinalIgnoreCase);

        foreach (var site in _nginxSites)
        {
            var names = site.ServerNames.Count > 0 ? site.ServerNames.Distinct().ToList() : new List<string> { "_" };
            var ports = site.Listens.Where(l => l.Port.HasValue).Select(l => l.Port!.Value).Distinct().ToList();
            var cert = _certificateCache.FirstOrDefault(c => site.CertPaths.Contains(c.Path, StringComparer.Ordinal));
            foreach (var name in names)
            {
                var row = BuildSite(name, ports, site.HasTls, _nginxServerType, _nginxVersion, "nginx -T", cert);
                row.SiteType = ClassifySite(site.ProxyTargets, site.RootPaths);
                row.RootPath = string.Join(", ", site.RootPaths);
                row.ProxyTarget = string.Join(", ", site.ProxyTargets);
                row.Upstream = ResolveNginxUpstreams(site.ProxyTargets);
                MergeSite(sites, row);
            }
        }

        foreach (var site in _apacheSites)
        {
            var names = site.Names.Count > 0 ? site.Names.Distinct().ToList() : new List<string> { "_" };
            var cert = _certificateCache.FirstOrDefault(c => site.CertPaths.Contains(c.Path, StringComparer.Ordinal));
            foreach (var name in names)
            {
                var row = BuildSite(name, site.Ports.ToList(), site.Ports.Contains(443), "Apache", _apacheVersion, site.ConfigPath, cert);
                row.SiteType = ClassifySite(site.ProxyTargets, site.RootPaths);
                row.RootPath = string.Join(", ", site.RootPaths);
                row.ProxyTarget = string.Join(", ", site.ProxyTargets);
                MergeSite(sites, row);
            }
        }

        foreach (var row in _extraSites)
            MergeSite(sites, row);

        foreach (var port in Ports.Where(p => p.Target.Length > 0))
        {
            var domain = port.Port is 443 or 8443 ? "localhost" : "127.0.0.1";
            var key = FindMatchingSite(sites, port.Port);
            if (key is not null)
            {
                var existing = sites[key];
                existing.Confidence = "高（配置 + 端口）";
                existing.Evidence = AppendEvidence(existing.Evidence, $"{port.Target} {port.HttpStatus}{port.HttpsStatus}");
                continue;
            }
            var row = BuildSite($"{domain}:{port.Port}", new[] { port.Port }, port.Target == "https",
                "HTTP", string.Empty, "端口探测", null);
            row.SiteType = "HTTP";
            row.Confidence = "低（仅端口）";
            row.Evidence = $"{port.Target} {port.HttpStatus}{port.HttpsStatus} {port.ServerHeader}".Trim();
            MergeSite(sites, row);
        }

        Sites.Clear();
        foreach (var site in sites.Values.OrderBy(s => s.Domain).ThenBy(s => s.Ports))
            Sites.Add(site);
        step.Detail = $"汇总 {Sites.Count} 个站点";
        OnPropertyChanged(nameof(CanSave));
        (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
        AddLog("汇总", step.Detail);
    }

    private static string BuildKey(SiteProbeSite site) =>
        $"{site.Domain.ToLowerInvariant()}|{site.Ports}|{site.Protocol}";

    private static void MergeSite(Dictionary<string, SiteProbeSite> sites, SiteProbeSite site)
    {
        var key = BuildKey(site);
        if (!sites.TryGetValue(key, out var existing))
        {
            sites[key] = site;
            return;
        }
        if (!existing.ServerType.Contains(site.ServerType, StringComparison.OrdinalIgnoreCase))
            existing.ServerType = string.IsNullOrEmpty(existing.ServerType) ? site.ServerType : existing.ServerType + " / " + site.ServerType;
        existing.Confidence = site.Confidence.Length > existing.Confidence.Length ? site.Confidence : existing.Confidence;
        existing.Evidence = AppendEvidence(existing.Evidence, site.Evidence);
        if (existing.CertificatePath.Length == 0) existing.CertificatePath = site.CertificatePath;
        if (existing.CertificateSubject.Length == 0) existing.CertificateSubject = site.CertificateSubject;
        if (existing.CertificateExpiry.Length == 0) existing.CertificateExpiry = site.CertificateExpiry;
        if (existing.SiteType.Length == 0) existing.SiteType = site.SiteType;
        if (existing.RootPath.Length == 0) existing.RootPath = site.RootPath;
        if (existing.ProxyTarget.Length == 0) existing.ProxyTarget = site.ProxyTarget;
        if (existing.Upstream.Length == 0) existing.Upstream = site.Upstream;
    }

    private static string ClassifySite(IEnumerable<string> proxyTargets, IEnumerable<string> rootPaths)
    {
        var proxies = proxyTargets.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (proxies.Any(p => p.Contains("php", StringComparison.OrdinalIgnoreCase) ||
                             p.Contains(":9000", StringComparison.Ordinal) ||
                             p.StartsWith("unix:", StringComparison.OrdinalIgnoreCase)))
            return "PHP-FPM";
        if (proxies.Count > 0)
            return "反向代理";
        if (rootPaths.Any(x => !string.IsNullOrWhiteSpace(x)))
            return "静态站点";
        return "Web服务";
    }

    private string ResolveNginxUpstreams(IEnumerable<string> proxyTargets)
    {
        var result = new List<string>();
        foreach (var target in proxyTargets)
        {
            var name = Regex.Match(target, @"^https?://([^/:]+)").Groups[1].Value;
            if (name.Length > 0 && _nginxUpstreams.TryGetValue(name, out var endpoints))
                result.Add($"{name}: {endpoints}");
        }
        return string.Join("; ", result.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string AppendEvidence(string current, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return current;
        return string.IsNullOrWhiteSpace(current) ? value : current + "；" + value;
    }

    private static SiteProbeSite BuildSite(string domain, IEnumerable<int> ports, bool tls,
        string serverType, string version, string source, SiteProbeCertificate? cert)
    {
        var portList = ports.Distinct().OrderBy(p => p).ToList();
        return new SiteProbeSite
        {
            Domain = string.IsNullOrWhiteSpace(domain) ? "_" : domain,
            Ports = string.Join(",", portList),
            Protocol = tls ? "HTTPS" : "HTTP",
            ServerType = serverType,
            ServerVersion = version,
            ConfigSource = source,
            CertificatePath = cert?.Path ?? string.Empty,
            CertificateSubject = cert?.Subject ?? string.Empty,
            CertificateExpiry = cert?.NotAfter ?? string.Empty,
            SiteType = "Web服务",
            Confidence = "中（仅配置）",
            Evidence = "配置"
        };
    }

    private string? FindMatchingSite(Dictionary<string, SiteProbeSite> sites, int port)
    {
        return sites.FirstOrDefault(kv => kv.Value.Ports.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(port.ToString())).Key;
    }

    private Dictionary<int, string> BuildHostMap()
    {
        var result = new Dictionary<int, string>();
        foreach (var site in _nginxSites)
        {
            var name = site.ServerNames.FirstOrDefault(n => n != "_" && n != "localhost") ?? site.ServerNames.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(name)) continue;
            foreach (var port in site.Listens.Where(l => l.Port.HasValue).Select(l => l.Port!.Value))
                result.TryAdd(port, name);
        }
        foreach (var site in _apacheSites)
        {
            var name = site.Names.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(name)) continue;
            foreach (var port in site.Ports) result.TryAdd(port, name);
        }
        return result;
    }

    private void ParseNginxUpstreams(string config)
    {
        foreach (Match block in Regex.Matches(config, @"(?ms)^\s*upstream\s+(\S+)\s*\{(.*?)^\s*\}"))
        {
            var name = block.Groups[1].Value.Trim();
            var endpoints = Regex.Matches(block.Groups[2].Value, @"(?m)^\s*server\s+([^;]+);")
                .Select(match => match.Groups[1].Value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (name.Length > 0 && endpoints.Count > 0)
                _nginxUpstreams[name] = string.Join(", ", endpoints);
        }
    }

    private void ParseApacheSites(string output)
    {
        ApacheSite? current = null;
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            var nameVhost = Regex.Match(line, @"port\s+(\d+)\s+namevhost\s+(\S+)\s+\(([^)]+)\)");
            if (nameVhost.Success && int.TryParse(nameVhost.Groups[1].Value, out var port))
            {
                current = new ApacheSite(NormalizeConfigPath(nameVhost.Groups[3].Value));
                current.Names.Add(nameVhost.Groups[2].Value);
                current.Ports.Add(port);
                _apacheSites.Add(current);
                continue;
            }
            var defaultServer = Regex.Match(line, @"default server\s+(\S+)\s+\(([^)]+)\)");
            if (defaultServer.Success)
            {
                current = new ApacheSite(NormalizeConfigPath(defaultServer.Groups[2].Value));
                current.Names.Add(defaultServer.Groups[1].Value);
                _apacheSites.Add(current);
                continue;
            }
            var alias = Regex.Match(line, @"^alias\s+(\S+)");
            if (alias.Success && current is not null)
                current.Names.Add(alias.Groups[1].Value);
        }

        foreach (var site in _apacheSites)
        {
            if (_apacheCertByFile.TryGetValue(site.ConfigPath, out var certs))
                foreach (var cert in certs)
                    if (!site.CertPaths.Contains(cert, StringComparer.Ordinal))
                        site.CertPaths.Add(cert);
            if (_apacheRootsByFile.TryGetValue(site.ConfigPath, out var roots))
                foreach (var root in roots)
                    if (!site.RootPaths.Contains(root, StringComparer.Ordinal))
                        site.RootPaths.Add(root);
            if (_apacheProxyByFile.TryGetValue(site.ConfigPath, out var proxies))
                foreach (var proxy in proxies)
                    if (!site.ProxyTargets.Contains(proxy, StringComparer.Ordinal))
                        site.ProxyTargets.Add(proxy);
        }
    }

    private static void AddFileValue(Dictionary<string, List<string>> map, string file, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!map.TryGetValue(file, out var list))
            map[file] = list = new List<string>();
        if (!list.Contains(value, StringComparer.Ordinal))
            list.Add(value);
    }

    private static string NormalizeConfigPath(string path) =>
        Regex.Replace(path.Trim().Trim('"', '\''), @":\d+$", string.Empty);

    private async Task<CommandResult?> RunAsync(string command, SiteDiscoveryStep step, int timeoutSeconds)
    {
        _cts?.Token.ThrowIfCancellationRequested();
        var full = PathPrefix + command;
        CommandResult result;
        if (_elevated && Server.SudoType != SudoType.None)
            result = await _ssh.ExecuteWithSudoAsync(Server, full, _cts?.Token ?? default);
        else
            result = await _ssh.ExecuteCommandAsync(Server, full, _cts?.Token ?? default, timeoutSeconds);
        if (IsPermissionDenied(result))
        {
            step.State = SiteDiscoveryStepState.NeedsElevation;
            step.Detail = "权限不足，可切换到「提权探测」重试";
            AddLog(step.Name, "权限不足");
            return null;
        }
        if (!result.Success && string.IsNullOrWhiteSpace(result.Output) && !string.IsNullOrWhiteSpace(result.Error))
            AddLog(step.Name, result.Error.Trim());
        return result;
    }

    private static bool IsPermissionDenied(CommandResult result)
    {
        var text = (result.Error + "\n" + result.Output).ToLowerInvariant();
        return text.Contains("permission denied", StringComparison.Ordinal) ||
               text.Contains("operation not permitted", StringComparison.Ordinal) ||
               text.Contains("you need to be root", StringComparison.Ordinal) ||
               text.Contains("must be root", StringComparison.Ordinal) ||
               text.Contains("authorization failed", StringComparison.Ordinal) ||
               text.Contains("authentication is required", StringComparison.Ordinal);
    }

    private static bool IsWebProcess(string process) =>
        process.Contains("nginx", StringComparison.OrdinalIgnoreCase) ||
        process.Contains("apache", StringComparison.OrdinalIgnoreCase) ||
        process.Contains("httpd", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownNonWebImage(string image) =>
        new[] { "redis", "mysql", "mariadb", "postgres", "mongo", "memcached", "rabbitmq", "kafka", "zookeeper" }
            .Any(marker => image.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string ClassifyContainerService(string image)
    {
        if (image.Contains("redis", StringComparison.OrdinalIgnoreCase)) return "Redis";
        if (image.Contains("mysql", StringComparison.OrdinalIgnoreCase)) return "MySQL";
        if (image.Contains("mariadb", StringComparison.OrdinalIgnoreCase)) return "MariaDB";
        if (image.Contains("postgres", StringComparison.OrdinalIgnoreCase)) return "PostgreSQL";
        if (image.Contains("mongo", StringComparison.OrdinalIgnoreCase)) return "MongoDB";
        if (image.Contains("memcached", StringComparison.OrdinalIgnoreCase)) return "Memcached";
        if (image.Contains("rabbitmq", StringComparison.OrdinalIgnoreCase)) return "RabbitMQ";
        if (image.Contains("kafka", StringComparison.OrdinalIgnoreCase)) return "Kafka";
        if (image.Contains("zookeeper", StringComparison.OrdinalIgnoreCase)) return "ZooKeeper";
        return "非 HTTP 服务";
    }

    private static bool IsLikelyWebImage(string image) =>
        new[]
        {
            "nginx", "apache", "httpd", "caddy", "traefik", "openresty", "haproxy", "envoy",
            "node", "gunicorn", "uvicorn", "tomcat", "jetty", "wildfly", "jboss", "php",
            "wordpress", "drupal", "ghost", "nextcloud", "grafana", "kibana", "prometheus",
            "minio", "sonarqube", "gitlab", "jenkins", "harbor", "aspnet", "dotnet"
        }.Any(marker => image.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsSafeCertPath(string path) =>
        path.StartsWith('/') && !path.Contains("..", StringComparison.Ordinal);

    private static string? ExtractPortBlock(string output, int port)
    {
        var marker = $"##PORT:{port}\n";
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            marker = $"##PORT:{port}\r\n";
            start = output.IndexOf(marker, StringComparison.Ordinal);
        }
        if (start < 0) return null;
        start += marker.Length;
        var next = output.IndexOf("##PORT:", start, StringComparison.Ordinal);
        return next < 0 ? output[start..] : output[start..next];
    }

    private static string? ExtractTlsBlock(string output, int port)
    {
        var marker = $"##TLS:{port}\n";
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            marker = $"##TLS:{port}\r\n";
            start = output.IndexOf(marker, StringComparison.Ordinal);
        }
        if (start < 0) return null;
        start += marker.Length;
        var next = output.IndexOf("##TLS:", start, StringComparison.Ordinal);
        return next < 0 ? output[start..] : output[start..next];
    }

    private static string FindOpenSslField(string text, string field)
    {
        var prefix = field + "=";
        return text.Replace("\r\n", "\n").Split('\n')
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?
            [prefix.Length..].Trim() ?? string.Empty;
    }

    private static string FindTlsVerify(string text) =>
        text.Replace("\r\n", "\n").Split('\n')
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.Contains("Verify return code", StringComparison.OrdinalIgnoreCase) ||
                                 x.Contains("verify error", StringComparison.OrdinalIgnoreCase) ||
                                 x.Contains("Verification", StringComparison.OrdinalIgnoreCase)) ?? "已连接";

    private static string FindStatusLine(string text) =>
        text.Replace("\r\n", "\n").Split('\n')
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

    private static string? FindHeader(string text, string header)
    {
        var prefix = header + ":";
        return text.Replace("\r\n", "\n").Split('\n')
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?
            [prefix.Length..].Trim();
    }

    private static string FindMetaValue(string text, string marker, string key)
    {
        var line = text.Replace("\r\n", "\n").Split('\n')
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.StartsWith(marker, StringComparison.Ordinal)) ?? string.Empty;
        var prefix = key + "=";
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?
            [prefix.Length..] ?? string.Empty;
    }

    private void AddLog(string category, string message)
    {
        LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {category}: {message}");
        LogText = string.Join(Environment.NewLine, LogLines.TakeLast(300));
    }

    private void UpdateProgress()
    {
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressMaximum));
        OnPropertyChanged(nameof(CanRetrySelectedStep));
        (RetryStepCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void Save()
    {
        try
        {
            var dir = ResultDirectory;
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir,
                $"{SafeFileNamePart(Server.Name)}-{Server.Id}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            var payload = new SiteProbeSnapshot
            {
                ServerId = Server.Id,
                ServerName = Server.Name,
                Host = Server.Host,
                Port = Server.Port,
                CreatedAt = DateTime.Now,
                Elevated = _elevated,
                Sites = Sites.ToList(),
                Ports = Ports.ToList(),
                Certificates = Certificates.ToList(),
                NonWebServices = NonWebServices.ToList()
            };
            File.WriteAllText(file, JsonSerializer.Serialize(payload, JsonOptions));
            SavedPath = file;
            StatusText = "结果已保存。";
            LoadHistory();
        }
        catch (Exception ex)
        {
            _dialogs.Error("保存失败：" + ex.Message, "站点探测");
        }
    }

    private void OpenFolder()
    {
        var dir = ResultDirectory;
        Directory.CreateDirectory(dir);
        NativeShell.OpenFolder(dir);
    }

    private void LoadHistory()
    {
        History.Clear();
        try
        {
            var dir = ResultDirectory;
            if (!Directory.Exists(dir))
                return;
            foreach (var file in Directory.GetFiles(dir, "*.json")
                         .Where(file => Path.GetFileName(file).Contains(Server.Id, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                try
                {
                    var snapshot = JsonSerializer.Deserialize<SiteProbeSnapshot>(File.ReadAllText(file), JsonOptions);
                    if (snapshot is null || !string.Equals(snapshot.ServerId, Server.Id, StringComparison.Ordinal))
                        continue;
                    History.Add(new SiteProbeHistoryItem
                    {
                        FilePath = file,
                        CreatedAt = snapshot.CreatedAt == default ? File.GetLastWriteTime(file) : snapshot.CreatedAt,
                        SiteCount = snapshot.Sites.Count,
                        Mode = snapshot.Elevated ? "提权探测" : "直接探测",
                        Snapshot = snapshot
                    });
                }
                catch
                {
                    // 单个损坏的历史文件不影响其他记录
                }
            }
        }
        catch
        {
            // 历史目录不可读时保持空列表
        }
        SelectedHistory = History.FirstOrDefault();
    }

    private void CompareHistory()
    {
        Diff.Clear();
        if (SelectedHistory?.Snapshot is not { } snapshot)
            return;
        var current = Sites.GroupBy(SiteKey).ToDictionary(g => g.Key, g => g.First());
        var previous = snapshot.Sites.GroupBy(SiteKey).ToDictionary(g => g.Key, g => g.First());
        foreach (var item in current.Where(item => !previous.ContainsKey(item.Key)))
            Diff.Add(new SiteProbeDiffItem
            {
                Change = "新增",
                Domain = item.Value.Domain,
                Detail = $"{item.Value.Protocol} {item.Value.Ports} · {item.Value.ServerType}"
            });
        foreach (var item in previous.Where(item => !current.ContainsKey(item.Key)))
            Diff.Add(new SiteProbeDiffItem
            {
                Change = "移除",
                Domain = item.Value.Domain,
                Detail = $"{item.Value.Protocol} {item.Value.Ports} · {item.Value.ServerType}"
            });
        foreach (var item in current.Where(item => previous.ContainsKey(item.Key)))
        {
            var old = previous[item.Key];
            var changed = !string.Equals(old.ServerType, item.Value.ServerType, StringComparison.OrdinalIgnoreCase) ||
                          !string.Equals(old.CertificatePath, item.Value.CertificatePath, StringComparison.Ordinal);
            if (!changed) continue;
            Diff.Add(new SiteProbeDiffItem
            {
                Change = "变化",
                Domain = item.Value.Domain,
                Detail = $"服务: {old.ServerType} → {item.Value.ServerType}；证书: {old.CertificatePath} → {item.Value.CertificatePath}"
            });
        }
        var previousPorts = (snapshot.Ports ?? new List<SiteProbePort>()).ToDictionary(p => p.Port);
        foreach (var port in Ports.Where(p => previousPorts.ContainsKey(p.Port)))
        {
            var old = previousPorts[port.Port];
            if (string.Equals(old.HttpStatus, port.HttpStatus, StringComparison.Ordinal) &&
                string.Equals(old.HttpsStatus, port.HttpsStatus, StringComparison.Ordinal) &&
                string.Equals(old.HttpTime, port.HttpTime, StringComparison.Ordinal) &&
                string.Equals(old.HttpsTime, port.HttpsTime, StringComparison.Ordinal))
                continue;
            Diff.Add(new SiteProbeDiffItem
            {
                Change = "端口",
                Domain = $"127.0.0.1:{port.Port}",
                Detail = $"HTTP {old.HttpStatus}({old.HttpTime}s) → {port.HttpStatus}({port.HttpTime}s)；" +
                         $"HTTPS {old.HttpsStatus}({old.HttpsTime}s) → {port.HttpsStatus}({port.HttpsTime}s)"
            });
        }
        (CopyDiffCommand as RelayCommand)?.RaiseCanExecuteChanged();
        StatusText = Diff.Count == 0 ? "与所选历史结果一致。" : $"对比完成：{Diff.Count} 项变化。";
    }

    private void DeleteHistory()
    {
        if (SelectedHistory is not { } item)
            return;
        if (!_dialogs.Confirm($"删除历史结果「{item.DisplayTime}」？", "HTTP站点探测"))
            return;
        try
        {
            File.Delete(item.FilePath);
            LoadHistory();
            StatusText = "历史结果已删除。";
        }
        catch (Exception ex)
        {
            _dialogs.Error("删除失败：" + ex.Message, "HTTP站点探测");
        }
    }

    private void ExportHistory()
    {
        var snapshot = SelectedHistory?.Snapshot ?? new SiteProbeSnapshot
        {
            ServerId = Server.Id,
            ServerName = Server.Name,
            Host = Server.Host,
            Port = Server.Port,
            CreatedAt = DateTime.Now,
            Elevated = _elevated,
            Sites = Sites.ToList(),
            Ports = Ports.ToList(),
            Certificates = Certificates.ToList(),
            NonWebServices = NonWebServices.ToList()
        };
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV 文件|*.csv|JSON 文件|*.json",
            FileName = $"{SafeFileNamePart(Server.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            AddExtension = true,
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog() != true)
            return;
        var isJson = dialog.FilterIndex == 2 ||
                     Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase);
        var target = Path.ChangeExtension(dialog.FileName, isJson ? ".json" : ".csv");
        try
        {
            if (isJson)
            {
                File.WriteAllText(target, JsonSerializer.Serialize(snapshot, JsonOptions));
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine("域名,端口,协议,服务,版本,配置来源,证书路径,置信度,证据");
                foreach (var site in snapshot.Sites)
                    sb.AppendLine(string.Join(",", new[]
                    {
                        Csv(site.Domain), Csv(site.Ports), Csv(site.Protocol), Csv(site.ServerType),
                        Csv(site.ServerVersion), Csv(site.ConfigSource), Csv(site.CertificatePath),
                        Csv(site.Confidence), Csv(site.Evidence)
                    }));
                File.WriteAllText(target, sb.ToString());
            }
            StatusText = "报告已导出：" + target;
            SavedPath = target;
        }
        catch (Exception ex)
        {
            _dialogs.Error("导出失败：" + ex.Message, "HTTP站点探测");
        }
    }

    private void CopyDiff()
    {
        if (Diff.Count == 0) return;
        System.Windows.Clipboard.SetText(string.Join(Environment.NewLine,
            Diff.Select(item => $"{item.Change}\t{item.Domain}\t{item.Detail}")));
        StatusText = $"已复制 {Diff.Count} 项对比结果。";
    }

    private static string Csv(string? value) =>
        "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

    private static string SiteKey(SiteProbeSite site) =>
        $"{site.Domain}|{site.Protocol}|{site.Ports}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static string ResultDirectory =>
        Path.Combine(ConfigPaths.AppDataDir, "site-probes");

    private static string SafeFileNamePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((value ?? string.Empty).Trim()
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray()).Trim('_', ' ');
        if (cleaned.Length == 0)
            cleaned = "server";
        return cleaned.Length <= 40 ? cleaned : cleaned[..40];
    }

    private async Task AuditAsync()
    {
        if (_audit is null) return;
        try
        {
            await _audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = Server.Id,
                ServerName = Server.Name,
                Tool = "site_probe",
                Command = "只读站点探测",
                Status = CommandStatus.Executed,
                Result = $"sites={Sites.Count}, ports={Ports.Count}, certs={Certificates.Count}",
                Category = AuditCategory.Exec
            });
        }
        catch
        {
            // 审计失败不影响探测结果
        }
    }

    private sealed record PortCandidate(int Port, string Bind, string Process, string Pid);

    private sealed class ApacheSite
    {
        public ApacheSite(string configPath) => ConfigPath = configPath;
        public string ConfigPath { get; }
        public List<string> Names { get; } = new();
        public HashSet<int> Ports { get; } = new();
        public List<string> CertPaths { get; } = new();
        public List<string> RootPaths { get; } = new();
        public List<string> ProxyTargets { get; } = new();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
