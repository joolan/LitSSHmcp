using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

/// <summary>目标地址选项：主机地址 / 内网地址（若目标服务器配置了内网地址）。</summary>
public sealed class RemoteCopyAddressOption
{
    public required string Label { get; init; }
    public required bool UseInternal { get; init; }
}

/// <summary>「复制到远程服务器」弹窗：源机直接推送到另一台服务器（走服务器间内网通道）。</summary>
public sealed class RemoteCopyViewModel : INotifyPropertyChanged
{
    private readonly ISshService _ssh;
    private readonly SshServerConfig _source;
    private List<SshServerConfig> _allServers = new();
    private CancellationTokenSource? _cts;

    /// <summary>构造：items 为 null 时进入「浏览模式」——左右两侧均为可切换的服务器文件浏览器，源侧现场多选。</summary>
    public RemoteCopyViewModel(SshServerConfig source, IEnumerable<RemoteFileItem>? items)
    {
        _source = source;
        _ssh = AppServiceFactory.CreateSshService();
        IsBrowserMode = items is null;
        Sources = new ObservableCollection<RemoteFileItem>(items ?? Enumerable.Empty<RemoteFileItem>());
        Targets = new ObservableCollection<SshServerConfig>();
        SourceTargets = new ObservableCollection<SshServerConfig>();
        Addresses = new ObservableCollection<RemoteCopyAddressOption>();

        StartCommand = new RelayCommand(_ => _ = StartAsync());
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsRunning);

        _ = LoadServersAsync();
    }

    public ObservableCollection<RemoteFileItem> Sources { get; }
    public ObservableCollection<SshServerConfig> Targets { get; }
    public ObservableCollection<SshServerConfig> SourceTargets { get; }
    public ObservableCollection<RemoteCopyAddressOption> Addresses { get; }

    /// <summary>true=浏览模式（从服务器管理打开，左右两侧为可切换的服务器浏览器）；false=已选列表模式（从会话文件面板打开）。</summary>
    public bool IsBrowserMode { get; }

    /// <summary>浏览模式下的源服务器文件浏览器（否则为 null）。</summary>
    public RemoteFileBrowserViewModel? SourceBrowser { get; private set; }

    /// <summary>所属窗口（由窗口设置），使本 VM 弹出的确认框仅模态于本窗口，不阻塞主程序。</summary>
    public Window? Owner { get; set; }

    private SshServerConfig? _selectedSource;

    /// <summary>浏览模式下左侧「源服务器」（可切换）。</summary>
    public SshServerConfig? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (ReferenceEquals(_selectedSource, value))
                return;
            _selectedSource = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentSource));
            OnPropertyChanged(nameof(SourceServerText));
            RebuildSourceBrowser();
            RebuildTargetsForSource();
        }
    }

    /// <summary>当前源服务器（浏览模式取左侧选择，否则固定为打开时传入的服务器）。</summary>
    public SshServerConfig CurrentSource => IsBrowserMode ? (_selectedSource ?? _source) : _source;

    public string SourceServerText => $"源服务器：{CurrentSource.Name} ({CurrentSource.Host})";

    /// <summary>实际要复制的源项（浏览模式取源浏览器当前多选）。</summary>
    private IReadOnlyList<RemoteFileItem> EffectiveSources =>
        IsBrowserMode ? (SourceBrowser?.SelectedItems ?? Array.Empty<RemoteFileItem>()) : Sources;

    public string SourceSummary
    {
        get
        {
            var items = EffectiveSources;
            var files = items.Count;
            var bytes = items.Sum(s => s.IsDirectory ? 0 : s.Size);
            var prefix = IsBrowserMode ? "已选择" : "待复制";
            if (IsBrowserMode && files == 0)
                return "已选择 0 项（请在下方浏览并选择文件 / 文件夹）";
            return bytes > 0
                ? $"{prefix} {files} 项 · 约 {FormatSize(bytes)}（目录内文件未计入）"
                : $"{prefix} {files} 项";
        }
    }

    private async Task LoadServersAsync()
    {
        try
        {
            var config = await AppServiceFactory.CreateConfigService().LoadConfigAsync();
            _allServers = config.Servers.ToList();

            if (IsBrowserMode)
            {
                foreach (var s in _allServers.Where(s => !s.Disabled))
                    SourceTargets.Add(s);

                SelectedSource = SourceTargets.FirstOrDefault(s => s.Id == _source.Id) ?? SourceTargets.FirstOrDefault();
                if (SourceTargets.Count == 0)
                    StatusMessage = "没有可用的服务器。";
                if (SelectedSource is null)
                    RebuildTargetsForSource();
            }
            else
            {
                RebuildTargetsForSource();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "加载服务器失败: " + ex.Message;
        }
    }

    /// <summary>按当前源服务器重建右侧目标列表（排除源、排除禁用）。</summary>
    private void RebuildTargetsForSource()
    {
        var previous = _selectedTarget;
        Targets.Clear();
        foreach (var s in _allServers.Where(s => !s.Disabled && s.Id != CurrentSource.Id))
            Targets.Add(s);

        SelectedTarget = Targets.FirstOrDefault(s => s.Id == previous?.Id) ?? Targets.FirstOrDefault();
        if (Targets.Count == 0)
            StatusMessage = "没有其它可选的服务器。";
    }

    private void RebuildSourceBrowser()
    {
        if (SourceBrowser is not null)
            SourceBrowser.PropertyChanged -= OnSourceBrowserChanged;

        if (!IsBrowserMode || SelectedSource is null)
        {
            SourceBrowser = null;
            OnPropertyChanged(nameof(SourceBrowser));
            return;
        }

        var browser = new RemoteFileBrowserViewModel(SelectedSource, _ssh);
        browser.PropertyChanged += OnSourceBrowserChanged;
        SourceBrowser = browser;
        OnPropertyChanged(nameof(SourceBrowser));
        OnPropertyChanged(nameof(SourceSummary));
        _ = browser.InitializeAsync();
    }

    private void OnSourceBrowserChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemoteFileBrowserViewModel.SelectedItems))
            OnPropertyChanged(nameof(SourceSummary));
    }

    private SshServerConfig? _selectedTarget;
    public SshServerConfig? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (ReferenceEquals(_selectedTarget, value))
                return;
            _selectedTarget = value;
            OnPropertyChanged();
            RebuildAddresses();
            RebuildTargetBrowser();
        }
    }

    private RemoteCopyAddressOption? _selectedAddress;
    public RemoteCopyAddressOption? SelectedAddress
    {
        get => _selectedAddress;
        set { _selectedAddress = value; OnPropertyChanged(); }
    }

    private RemoteFileBrowserViewModel? _targetBrowser;
    public RemoteFileBrowserViewModel? TargetBrowser
    {
        get => _targetBrowser;
        private set { _targetBrowser = value; OnPropertyChanged(); }
    }

    private string _targetDirectory = string.Empty;
    public string TargetDirectory
    {
        get => _targetDirectory;
        set { _targetDirectory = value; OnPropertyChanged(); }
    }

    private bool _compress = true;
    public bool Compress
    {
        get => _compress;
        set { _compress = value; OnPropertyChanged(); }
    }

    private bool _overwrite = true;
    public bool Overwrite
    {
        get => _overwrite;
        set { _overwrite = value; OnPropertyChanged(); }
    }

    private bool _allowRelayFallback = true;
    public bool AllowRelayFallback
    {
        get => _allowRelayFallback;
        set { _allowRelayFallback = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<string> AuthModes { get; } = new[] { "自动（优先免密）", "仅密钥免密", "使用目标密码" };

    private int _authModeIndex;
    public int AuthModeIndex
    {
        get => _authModeIndex;
        set { _authModeIndex = value; OnPropertyChanged(); }
    }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            _isRunning = value;
            OnPropertyChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private double _progressPercent;
    public double ProgressPercent
    {
        get => _progressPercent;
        private set { _progressPercent = value; OnPropertyChanged(); }
    }

    private bool _isIndeterminate;
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set { _isIndeterminate = value; OnPropertyChanged(); }
    }

    private string _statusMessage = "选择目标服务器与目录后点击「开始传输」。";
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }

    private void RebuildAddresses()
    {
        Addresses.Clear();
        if (SelectedTarget is null)
        {
            SelectedAddress = null;
            return;
        }

        var hasInternal = RemoteCopyCommandBuilder.HasInternal(SelectedTarget);
        if (hasInternal)
            Addresses.Add(new RemoteCopyAddressOption { Label = $"内网地址（{SelectedTarget.InternalHost}:{SelectedTarget.InternalPort ?? SelectedTarget.Port}）", UseInternal = true });
        Addresses.Add(new RemoteCopyAddressOption { Label = $"主机地址（{SelectedTarget.Host}:{SelectedTarget.Port}）", UseInternal = false });
        SelectedAddress = Addresses[0];
    }

    private void RebuildTargetBrowser()
    {
        if (TargetBrowser is not null)
            TargetBrowser.PropertyChanged -= OnTargetBrowserChanged;

        if (SelectedTarget is null)
        {
            TargetBrowser = null;
            TargetDirectory = string.Empty;
            return;
        }

        var browser = new RemoteFileBrowserViewModel(SelectedTarget, _ssh);
        browser.PropertyChanged += OnTargetBrowserChanged;
        TargetBrowser = browser;
        TargetDirectory = browser.CurrentPath;
        _ = browser.InitializeAsync();
    }

    private void OnTargetBrowserChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemoteFileBrowserViewModel.CurrentPath) && TargetBrowser is not null)
            TargetDirectory = TargetBrowser.CurrentPath;
    }

    private async Task StartAsync()
    {
        if (IsRunning)
        {
            StatusMessage = "正在传输中，请等待完成或点「关闭」。";
            return;
        }
        if (EffectiveSources.Count == 0)
        {
            StatusMessage = IsBrowserMode
                ? "请先在源服务器文件列表中选择要复制的文件 / 文件夹。"
                : "请先选择要复制的文件 / 文件夹。";
            return;
        }
        if (SelectedTarget is null)
        {
            StatusMessage = "请先选择目标服务器（仅列出未禁用的服务器）。";
            return;
        }
        if (string.IsNullOrWhiteSpace(TargetDirectory))
        {
            StatusMessage = "请填写或选择目标目录。";
            return;
        }

        var useInternal = SelectedAddress?.UseInternal ?? false;
        var (host, port) = RemoteCopyCommandBuilder.ResolveEndpoint(SelectedTarget, useInternal);

        RemoteCopyRequest BuildRequest(RemoteCopyTransport transport) => new()
        {
            Source = CurrentSource,
            SourcePaths = EffectiveSources.Select(s => s.FullName).ToList(),
            Target = SelectedTarget!,
            TargetHost = host,
            TargetPort = port,
            TargetDirectory = TargetDirectory.Trim(),
            Compress = Compress,
            Overwrite = Overwrite,
            AuthMode = AuthModeIndex switch
            {
                1 => RemoteCopyAuthMode.KeyOnly,
                2 => RemoteCopyAuthMode.Password,
                _ => RemoteCopyAuthMode.Auto
            },
            Transport = transport,
            AllowRelayFallback = AllowRelayFallback,
            TimeoutSeconds = 3600
        };

        IsRunning = true;
        IsIndeterminate = true;
        ProgressPercent = 0;
        StatusMessage = $"正在从「{CurrentSource.Name}」推送到「{SelectedTarget.Name}」…";

        _cts = new CancellationTokenSource();
        var progress = new Progress<FileTransferProgress>(p =>
        {
            IsIndeterminate = p.TotalBytes <= 0;
            ProgressPercent = p.Percentage;
        });

        try
        {
            var result = await _ssh.CopyRemoteToRemoteAsync(BuildRequest(RemoteCopyTransport.Auto), progress, _cts.Token);
            var relayHandled = false;

            // 源机缺 sshpass：提示安装（给出各发行版命令）→ 安装后重试，或改用内存中转 / 取消
            while (!result.Success && result.NeedsSshpassInstall)
            {
                var (hint, commands) = await BuildSshpassHintAsync();
                var installChoice = SshpassInstallDialog.Show(Owner ?? ActiveWindow(), hint, commands, AllowRelayFallback);

                if (installChoice == SshpassInstallChoice.Retry)
                {
                    StatusMessage = "已按确认重试直连…";
                    IsIndeterminate = true;
                    ProgressPercent = 0;
                    result = await _ssh.CopyRemoteToRemoteAsync(BuildRequest(RemoteCopyTransport.Auto), progress, _cts.Token);
                    continue;
                }

                if (installChoice == SshpassInstallChoice.Relay && AllowRelayFallback)
                {
                    StatusMessage = "正在经本机内存中转…";
                    IsIndeterminate = true;
                    ProgressPercent = 0;
                    result = await _ssh.CopyRemoteToRemoteAsync(BuildRequest(RemoteCopyTransport.Relay), progress, _cts.Token);
                }
                else
                {
                    StatusMessage = $"已取消：源服务器「{CurrentSource.Name}」未安装 sshpass。";
                }

                relayHandled = true;
                break;
            }

            // 直连不可用：交由用户决定是否改用内存中转（不自动回退）
            if (!relayHandled && !result.Success && result.NeedsRelayConfirmation)
            {
                var directError = string.IsNullOrEmpty(result.Error) ? "直连不可用" : result.Error;
                var choice = MessageBox.Show(Owner,
                    $"直连传输不可用：\n{directError}\n\n是否改用「经本机内存中转」继续？\n（不落盘，但速度受本机链路限制，且不是服务器间直连）",
                    "复制到远程服务器", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (choice == MessageBoxResult.Yes)
                {
                    StatusMessage = "正在经本机内存中转…";
                    IsIndeterminate = true;
                    ProgressPercent = 0;
                    result = await _ssh.CopyRemoteToRemoteAsync(BuildRequest(RemoteCopyTransport.Relay), progress, _cts.Token);
                }
                else
                {
                    StatusMessage = $"已取消（未使用内存中转）：{directError}";
                }
            }

            if (result.Success)
            {
                ProgressPercent = 100;
                IsIndeterminate = false;
                var note = string.IsNullOrEmpty(result.Warning) ? string.Empty : $"（{result.Warning}）";
                var skip = result.ItemsSkipped > 0 ? $"，跳过 {result.ItemsSkipped} 个同名文件" : string.Empty;
                StatusMessage = $"已完成：{result.Strategy} 传输 {result.ItemsSucceeded}/{result.ItemsTotal} 项{skip}{note}";
            }
            else if (!result.NeedsRelayConfirmation)
            {
                IsIndeterminate = false;
                StatusMessage = $"失败（{result.ErrorKind}）：{result.Error}";
            }
            else
            {
                IsIndeterminate = false;
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = "失败: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            await RefreshTargetAsync();
        }
    }

    /// <summary>传输后刷新目标服务器文件列表，让用户立即看到结果。</summary>
    private async Task RefreshTargetAsync()
    {
        if (TargetBrowser is null)
            return;
        try
        {
            await TargetBrowser.LoadAsync(TargetBrowser.CurrentPath);
        }
        catch
        {
            // 刷新失败不影响传输结果
        }
    }

    /// <summary>探测源服务器的包管理器并给出各发行版安装 sshpass 的命令。</summary>
    private async Task<(string Hint, string Commands)> BuildSshpassHintAsync()
    {
        var detected = new List<string>();
        try
        {
            var res = await _ssh.ExecuteCommandAsync(CurrentSource,
                "for c in apt-get dnf yum zypper pacman apk; do command -v $c >/dev/null 2>&1 && echo HAS:$c; done");
            if (res.Success && !string.IsNullOrWhiteSpace(res.Output))
                detected = res.Output.Replace("\r", string.Empty)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Where(l => l.StartsWith("HAS:", StringComparison.Ordinal))
                    .Select(l => l[4..].Trim())
                    .Where(l => l.Length > 0)
                    .Distinct()
                    .ToList();
        }
        catch
        {
            // 探测失败则退回通用列表
        }

        var hint = detected.Count > 0
            ? $"源服务器「{CurrentSource.Name}」未安装 sshpass，无法用「目标密码」直连目标。检测到可用包管理器：{string.Join("、", detected)}，请安装后点「已安装，重试」。"
            : $"源服务器「{CurrentSource.Name}」未安装 sshpass，无法用「目标密码」直连目标。请按对应发行版安装后点「已安装，重试」。";

        var sb = new StringBuilder();
        sb.AppendLine("# Debian / Ubuntu");
        sb.AppendLine("sudo apt-get update && sudo apt-get install -y sshpass");
        sb.AppendLine();
        sb.AppendLine("# CentOS 7 / RHEL 7（先安装 EPEL）");
        sb.AppendLine("sudo yum install -y epel-release && sudo yum install -y sshpass");
        sb.AppendLine();
        sb.AppendLine("# CentOS 8+ / RHEL 8+ / Rocky / AlmaLinux / Fedora");
        sb.AppendLine("sudo dnf install -y sshpass");
        sb.AppendLine();
        sb.AppendLine("# openSUSE / SLES");
        sb.AppendLine("sudo zypper install -y sshpass");
        sb.AppendLine();
        sb.AppendLine("# Arch / Manjaro");
        sb.AppendLine("sudo pacman -S --noconfirm sshpass");
        sb.AppendLine();
        sb.AppendLine("# Alpine");
        sb.AppendLine("sudo apk add --no-cache sshpass");

        return (hint, sb.ToString().TrimEnd());
    }

    private static Window? ActiveWindow() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault();

    /// <summary>窗口关闭时调用：若正在传输则取消。</summary>
    public void CancelIfRunning()
    {
        if (IsRunning)
            Cancel();
    }

    private void Cancel()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var u = 0;
        while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{size:0.#} {units[u]}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
