using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Snapshot;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Win32;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel : INotifyPropertyChanged, IConfigReloadable
{
    private readonly IAppServices _services;
    private readonly IConfigService _configService;
    private readonly ISshService _sshService;
    private readonly PortForwardService _portForwardService;
    private readonly IUserDialogService _dialogs;
    private readonly IWindowService _windows;

    // 快照: 采集服务实例须长期复用(内存单飞锁绑定实例); 库供历史窗口读取。
    private readonly ISnapshotService _snapshotService;
    private readonly ISnapshotStore _snapshotStore;
    private bool _snapshotBusy;

    private SshServerConfig? _selectedServer;
    private object? _selectedSession;
    private string _statusMessage = string.Empty;

    public ObservableCollection<SshServerConfig> Servers { get; } = new();
    public ObservableCollection<object> Sessions { get; } = new();

    /// <summary>主会话窗口实际显示的会话（= Sessions 去掉已分离为独立窗口的）；浮窗按会话独立承载。</summary>
    public ObservableCollection<object> DockedSessions { get; } = new();

    /// <summary>按「分组」字段分组的服务器视图（连接管理器）。</summary>
    public System.ComponentModel.ICollectionView GroupedServers { get; }

    public ICommand LoadServersCommand { get; }
    public ICommand AddServerCommand { get; }
    public ICommand EditServerCommand { get; }
    public ICommand DeleteServerCommand { get; }
    public ICommand CloseSessionCommand { get; }
    public ICommand OpenAuditCommand { get; }
    public ICommand OpenTopologyCommand { get; }
    public ICommand OpenAgentCommand { get; }
    public ICommand NewAiSessionCommand { get; }
    public ICommand NavigateHomeCommand { get; }
    public ICommand NavigateServersCommand { get; }
    public ICommand NavigateDataSourcesCommand { get; }
    public ICommand NavigateApplicationsCommand { get; }
    public ICommand NavigateRemoteDesktopsCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenSessionManagerCommand { get; }
    public ICommand OpenTerminalCommand { get; }
    public ICommand OpenBatchExecCommand { get; }
    public ICommand OpenMonitorCommand { get; }
    public ICommand OpenTerminalLogsCommand { get; }
    public ICommand OpenPortForwardCommand { get; }
    public ICommand OpenSyncCommand { get; }
    public ICommand OpenTriggerCommand { get; }
    public ICommand OpenSftpManagerCommand { get; }
    public ICommand OpenRemoteCopyCommand { get; }
    public ICommand OpenProxiedBrowserCommand { get; }
    public ICommand AskAgentCommand { get; }
    public ICommand SnapshotRefreshCommand { get; }
    public ICommand OpenSnapshotHistoryCommand { get; }

    public MainViewModel(IUserDialogService? dialogs = null, IWindowService? windows = null,
        IAppServices? services = null)
    {
        _services = services ?? new AppServices(dialogs, windows);
        _dialogs = _services.Dialogs;
        _windows = _services.Windows;
        _configService = _services.Config;
        _portForwardService = _services.PortForward;
        _snapshotService = _services.CreateSnapshotService();
        _snapshotStore = _services.CreateSnapshotStore();
        // 与数据源 / 端口转发 / 拓扑发现一致：携带 known-hosts 与安全设置（Tofu/Strict/Off），
        // 避免主界面终端绕过主机密钥校验、而弹窗"测试连接"走另一套策略。
        _sshService = _services.CreateSshService();
        AiTerminals = new Services.LocalTerminal.AiTerminalBroker(
            name => CreateLocalTerminal(isAi: true, aiName: name),
            RemoveLocalTerminal,
            terminal =>
            {
                SelectedSession = terminal;
                ShowSessionWindow();
            });

        LoadServersCommand = new RelayCommand(_ => LoadServers());
        AddServerCommand = new RelayCommand(_ => AddServer());
        EditServerCommand = new RelayCommand(_ => EditServer(), _ => SelectedServer != null);
        DeleteServerCommand = new RelayCommand(_ => DeleteServer(), _ => SelectedServer != null);
        CloseSessionCommand = new RelayCommand(p => CloseSession(p));
        OpenAuditCommand = new RelayCommand(_ => OpenAudit());
        OpenTopologyCommand = new RelayCommand(_ => OpenTopology());
        OpenAgentCommand = new RelayCommand(_ => OpenAgent());
        NewAiSessionCommand = new RelayCommand(_ => NewAiSessionForServer(SelectedServer), _ => SelectedServer != null);
        NavigateHomeCommand = new RelayCommand(_ => NavigateHome());
        NavigateServersCommand = new RelayCommand(_ => CurrentPage = MainPage.Servers);
        NavigateDataSourcesCommand = new RelayCommand(_ => NavigateDataSources());
        NavigateApplicationsCommand = new RelayCommand(_ => NavigateApplications());
        NavigateRemoteDesktopsCommand = new RelayCommand(_ => NavigateRemoteDesktops());
        OpenSettingsCommand = new RelayCommand(_ => OpenSettings());
        OpenSessionManagerCommand = new RelayCommand(_ => ShowSessionWindow());
        OpenTerminalCommand = new RelayCommand(_ => ConnectTerminal(SelectedServer), _ => SelectedServer != null);
        OpenBatchExecCommand = new RelayCommand(_ => OpenBatchExec());
        OpenMonitorCommand = new RelayCommand(_ => OpenMonitor(SelectedServer), _ => SelectedServer != null);
        OpenTerminalLogsCommand = new RelayCommand(_ => OpenTerminalLogs());
        OpenPortForwardCommand = new RelayCommand(_ => OpenPortForward());
        OpenSyncCommand = new RelayCommand(_ => OpenSync());
        OpenTriggerCommand = new RelayCommand(_ => OpenTrigger());
        OpenSftpManagerCommand = new RelayCommand(_ => OpenSftpManager(SelectedServer), _ => SelectedServer != null);
        OpenRemoteCopyCommand = new RelayCommand(_ => OpenRemoteCopy(SelectedServer), _ => SelectedServer != null);
        OpenProxiedBrowserCommand = new RelayCommand(_ => OpenProxiedBrowser(SelectedServer), _ => SelectedServer is { Disabled: false });
        AskAgentCommand = new RelayCommand(_ => AskAgent());
        SnapshotRefreshCommand = new RelayCommand(_ => RefreshSnapshot(SelectedServer), _ => SelectedServer != null && !_snapshotBusy);
        OpenSnapshotHistoryCommand = new RelayCommand(_ => OpenSnapshotHistory(SelectedServer), _ => SelectedServer != null);

        // 看板：后台隧道起停（含自动重连）实时刷新"运行中"计数（事件在线程池触发 → 调度到 UI 线程）
        _portForwardService.StateChanged += (_, _, _) =>
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                PortForwardRunningCount = _portForwardService.ActiveIds.Count));

        Sessions.CollectionChanged += (_, _) =>
        {
            RebuildDockedSessions();
            OnPropertyChanged(nameof(HasSessions));
            OnPropertyChanged(nameof(ActiveSessionCount));
        };

        // 服务器列表按「分组」字段分组（连接管理器树形分组）
        GroupedServers = System.Windows.Data.CollectionViewSource.GetDefaultView(Servers);
        GroupedServers.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(SshServerConfig.Group)));
        GroupedServers.Filter = FilterServer;

        _sshTileHeight = Services.UiPrefs.GetDouble("ssh.tileHeight", 360);

        // 启动时建表并回收上次崩溃遗留的 Running 快照；失败不阻断（首次读写还会惰性建表兜底）
        _ = InitializeSnapshotStoreAsync();

        LoadServers();
    }

    private async Task InitializeSnapshotStoreAsync()
    {
        try
        {
            await _snapshotStore.InitializeAsync();
        }
        catch
        {
            // 忽略: 具体错误会在使用快照功能时以界面状态呈现
        }
    }

    public SshServerConfig? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (_selectedServer == value) return;
            _selectedServer = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedServer));
            OnPropertyChanged(nameof(SelectedServerTagsText));
            (EditServerCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteServerCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenTerminalCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenMonitorCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenSftpManagerCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenRemoteCopyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenProxiedBrowserCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SnapshotRefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenSnapshotHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (NewAiSessionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelectedServer => SelectedServer != null;

    public string SelectedServerTagsText => SelectedServer is { Tags.Length: > 0 } server
        ? string.Join(" · ", server.Tags)
        : "—";

    public object? SelectedSession
    {
        get => _selectedSession;
        set { _selectedSession = value; OnPropertyChanged(); }
    }

    public bool HasSessions => Sessions.Count > 0;

    /// <summary>主窗口是否有可显示（未分离）的会话；用于空状态提示。</summary>
    public bool HasDockedSessions => DockedSessions.Count > 0;

    // ---- 主页数据看板 ----

    /// <summary>服务器总数。</summary>
    public int ServerCount => Servers.Count;

    /// <summary>已禁用服务器数。</summary>
    public int DisabledServerCount => Servers.Count(s => s.Disabled);

    public bool HasDisabledServers => DisabledServerCount > 0;

    private int _datasourceCount;
    public int DatasourceCount
    {
        get => _datasourceCount;
        private set { _datasourceCount = value; OnPropertyChanged(); }
    }

    private int _applicationCount;
    public int ApplicationCount
    {
        get => _applicationCount;
        private set { _applicationCount = value; OnPropertyChanged(); }
    }

    private int _remoteDesktopCount;
    /// <summary>远程桌面（RDP / VNC）连接总数。</summary>
    public int RemoteDesktopCount
    {
        get => _remoteDesktopCount;
        private set { _remoteDesktopCount = value; OnPropertyChanged(); }
    }

    private int _portForwardCount;
    /// <summary>端口转发规则总数。</summary>
    public int PortForwardCount
    {
        get => _portForwardCount;
        private set { _portForwardCount = value; OnPropertyChanged(); }
    }

    private int _portForwardRunningCount;
    /// <summary>当前运行中的端口转发数（含窗口关闭后的后台隧道）。</summary>
    public int PortForwardRunningCount
    {
        get => _portForwardRunningCount;
        private set
        {
            _portForwardRunningCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasRunningPortForwards));
        }
    }

    public bool HasRunningPortForwards => PortForwardRunningCount > 0;

    private int _syncTaskCount;
    /// <summary>文件夹同步任务总数。</summary>
    public int SyncTaskCount
    {
        get => _syncTaskCount;
        private set { _syncTaskCount = value; OnPropertyChanged(); }
    }

    private int _syncTaskEnabledCount;
    /// <summary>已启用的同步任务数。</summary>
    public int SyncTaskEnabledCount
    {
        get => _syncTaskEnabledCount;
        private set
        {
            _syncTaskEnabledCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasEnabledSyncTasks));
        }
    }

    public bool HasEnabledSyncTasks => SyncTaskEnabledCount > 0;

    private int _triggerCount;
    /// <summary>触发事件总数。</summary>
    public int TriggerCount
    {
        get => _triggerCount;
        private set { _triggerCount = value; OnPropertyChanged(); }
    }

    private int _triggerEnabledCount;
    /// <summary>已启用的触发事件数。</summary>
    public int TriggerEnabledCount
    {
        get => _triggerEnabledCount;
        private set
        {
            _triggerEnabledCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasEnabledTriggers));
        }
    }

    public bool HasEnabledTriggers => TriggerEnabledCount > 0;

    private string _homeQuestion = string.Empty;

    /// <summary>主页「AI 快捷提问」输入内容。</summary>
    public string HomeQuestion
    {
        get => _homeQuestion;
        set
        {
            _homeQuestion = value;
            OnPropertyChanged();
            if (!string.IsNullOrWhiteSpace(value) && HomeStatusMessage.Length > 0)
                HomeStatusMessage = string.Empty;
        }
    }

    private string _homeStatusMessage = string.Empty;

    /// <summary>主页 AI 快捷提问的状态提示（空提交、已填入助手等）。</summary>
    public string HomeStatusMessage
    {
        get => _homeStatusMessage;
        private set
        {
            _homeStatusMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasHomeStatus));
        }
    }

    public bool HasHomeStatus => HomeStatusMessage.Length > 0;

    private bool _homeStatusIsError;
    public bool HomeStatusIsError
    {
        get => _homeStatusIsError;
        private set { _homeStatusIsError = value; OnPropertyChanged(); }
    }

    private MainPage _currentPage = MainPage.Home;

    /// <summary>右侧主区域当前展示的页面。</summary>
    public MainPage CurrentPage
    {
        get => _currentPage;
        set
        {
            if (_currentPage == value) return;
            _currentPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowAgentBadge));
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    // ---- SSH 会话管理布局 ----

    private SessionLayout _sshLayout = SessionLayout.Tabs;
    public SessionLayout SshLayout
    {
        get => _sshLayout;
        set
        {
            if (_sshLayout == value) return;
            _sshLayout = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSshTabbed));
            OnPropertyChanged(nameof(IsSshTiled));
            OnPropertyChanged(nameof(SshTileColumns));
        }
    }

    public bool IsSshTabbed => SshLayout == SessionLayout.Tabs;
    public bool IsSshTiled => !IsSshTabbed;

    public int SshTileColumns => SshLayout switch
    {
        SessionLayout.Tile3 => 3,
        SessionLayout.Tile2 => 2,
        _ => 1
    };

    private double _sshTileHeight = 360;
    public double SshTileHeight
    {
        get => _sshTileHeight;
        set
        {
            var clamped = Math.Clamp(value, 200, 900);
            if (Math.Abs(clamped - _sshTileHeight) < 0.1) return;
            _sshTileHeight = clamped;
            OnPropertyChanged();
            Services.UiPrefs.SetDouble("ssh.tileHeight", clamped);
        }
    }

    private double _sshTileCardHeight = 360;

    /// <summary>平铺卡片实际高度：内容未溢出时铺满可用高度，溢出时用用户设定行高（并出现滚动条）。</summary>
    public double SshTileCardHeight
    {
        get => _sshTileCardHeight;
        set
        {
            if (Math.Abs(value - _sshTileCardHeight) < 0.1) return;
            _sshTileCardHeight = value;
            OnPropertyChanged();
        }
    }

    /// <summary>同一服务器最多同时打开的交互式终端数。</summary>
    public const int MaxTerminalsPerServer = 4;

    /// <summary>打开一个交互式终端标签（PTY shell）；同一服务器最多 <see cref="MaxTerminalsPerServer"/> 个。</summary>
    public void ConnectTerminal(SshServerConfig? server)
    {
        if (server == null) return;

        if (server.Disabled)
        {
            StatusMessage = $"服务器 {server.Name} 已禁用, 不允许连接。";
            return;
        }

        var terminals = Sessions.OfType<TerminalSessionViewModel>()
            .Where(t => t.Server.Id == server.Id)
            .ToList();

        if (terminals.Count >= MaxTerminalsPerServer)
        {
            SelectedSession = terminals[0];
            ShowSessionWindow();
            StatusMessage = $"服务器 {server.Name} 最多同时打开 {MaxTerminalsPerServer} 个终端。";
            return;
        }

        var used = terminals.Select(t => t.Ordinal).ToHashSet();
        var ordinal = Enumerable.Range(1, MaxTerminalsPerServer).First(i => !used.Contains(i));
        var vm = new TerminalSessionViewModel(server, _sshService, ordinal: ordinal);
        Sessions.Add(vm);
        SelectedSession = vm;
        ShowSessionWindow();
        StatusMessage = $"已打开终端: {vm.Title} ({terminals.Count + 1}/{MaxTerminalsPerServer})";
    }

    /// <summary>测试临时 SSH 连接（不读取/写入配置）。</summary>
    public Task<ConnectionProbeResult> ProbeTemporaryServerAsync(SshServerConfig server)
        => _sshService.ProbeConnectionAsync(server);

    /// <summary>打开临时 SSH 会话：服务器对象只存在于内存，不写入配置。</summary>
    public void ConnectTemporaryServer(SshServerConfig server) => ConnectTerminal(server);

    private readonly Dictionary<string, LocalTerminalSessionViewModel> _aiTerminals = new(StringComparer.Ordinal);
    private int _manualLocalSeq;

    public Services.LocalTerminal.IAiTerminalBroker AiTerminals { get; }

    /// <summary>手动打开一个新的本机 PowerShell 终端标签（可多次打开，互不影响）。</summary>
    public LocalTerminalSessionViewModel ConnectLocalTerminal()
    {
        var vm = CreateLocalTerminal(isAi: false);
        SelectedSession = vm;
        ShowSessionWindow();
        return vm;
    }

    private LocalTerminalSessionViewModel CreateLocalTerminal(bool isAi, string? aiName = null)
    {
        var vm = new LocalTerminalSessionViewModel(isAi, ++_manualLocalSeq) { SessionName = aiName };
        Sessions.Add(vm);
        _ = vm.EnsureStartedAsync(vm.Model.Cols, vm.Model.Rows);
        return vm;
    }

    private void RemoveLocalTerminal(LocalTerminalSessionViewModel terminal)
    {
        _ = terminal.DisposeAsync();
        if (_floatWindows.TryGetValue(terminal, out var floatWin))
        {
            _floatWindows.Remove(terminal);
            floatWin.SuppressDock();
            floatWin.Close();
        }
        if (!Sessions.Contains(terminal))
            return;
        var index = DockedSessions.IndexOf(terminal);
        var wasSelected = ReferenceEquals(SelectedSession, terminal);
        Sessions.Remove(terminal);
        RebuildDockedSessions();
        EnsureDockedSelection(wasSelected ? index : null);
    }

    /// <summary>
    /// 按 AI 会话键解析共享本机终端：同一会话复用同一个终端；若记住的终端已不存在
    /// （标签被关、进程退出/失效）则重建一个（标题带 [AI-进程Id] 便于区分）。
    /// </summary>
    public LocalTerminalSessionViewModel? ResolveSessionTerminal(string key)
    {
        key ??= "default";
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            return dispatcher.Invoke(() => ResolveSessionTerminal(key));

        if (_aiTerminals.TryGetValue(key, out var existing))
        {
            if (IsTerminalAlive(existing))
                return existing;

            _aiTerminals.Remove(key);
            if (Sessions.Contains(existing))
            {
                _ = existing.DisposeAsync();
                Sessions.Remove(existing);
                RebuildDockedSessions();
            }
        }

        var created = CreateLocalTerminal(isAi: true);
        _aiTerminals[key] = created;
        SelectedSession = created;
        ShowSessionWindow();
        return created;
    }

    /// <summary>记住的终端是否仍然有效（在会话列表中、已连接、且其进程仍存在）。</summary>
    private bool IsTerminalAlive(LocalTerminalSessionViewModel? vm)
    {
        if (vm is null || !Sessions.Contains(vm) || !vm.IsConnected)
            return false;
        var pid = vm.ProcessId;
        if (pid <= 0)
            return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    // SSH 会话管理窗口（独立非模态窗口：多标签，同一服务器最多 4 个终端；不设 Owner/不置顶）
    private SshSessionWindow? _sessionWindow;

    private void ShowSessionWindow()
    {
        if (_sessionWindow is { IsLoaded: true })
        {
            _sessionWindow.Activate();
            return;
        }

        _sessionWindow = _windows.CreateSessionWindow(this);
        _sessionWindow.Closed += (_, _) =>
        {
            _sessionWindow = null;
            foreach (var w in _floatWindows.Values.ToList())
            {
                w.SuppressDock();
                w.Close();
            }
            _floatWindows.Clear();
            foreach (var term in Sessions.OfType<TerminalSessionViewModel>().ToList())
                _ = term.DisposeAsync();
            AiTerminals.CloseAll();
            foreach (var local in Sessions.OfType<LocalTerminalSessionViewModel>().ToList())
                _ = local.DisposeAsync();
            Sessions.Clear();
            _aiTerminals.Clear();
            SelectedSession = null;
        };
        _sessionWindow.Show();
    }

    // ---- 会话分离为独立窗口（拖出会话窗口 / 右键「分离为独立窗口」） ----

    private readonly Dictionary<object, SshSessionFloatWindow> _floatWindows = new();

    /// <summary>把会话分离为一个独立、紧凑、可缩放的窗口；主窗口不再显示该会话。</summary>
    public void FloatSession(object? session)
    {
        if (session is null)
            return;

        if (_floatWindows.TryGetValue(session, out var existing) && existing.IsLoaded)
        {
            existing.Activate();
            return;
        }

        var window = _windows.CreateSessionFloat(this, session);
        window.DockRequested += DockSession;
        window.CloseRequested += CloseSession;
        var index = DockedSessions.IndexOf(session);
        var wasSelected = ReferenceEquals(SelectedSession, session);
        _floatWindows[session] = window;
        RebuildDockedSessions();
        EnsureDockedSelection(wasSelected ? index : null);

        window.Show();
        StatusMessage = $"已分离为独立窗口: {TitleOf(session)}";
    }

    /// <summary>把独立窗口的会话收回主窗口。</summary>
    private void DockSession(object session)
    {
        if (_floatWindows.TryGetValue(session, out var window))
        {
            _floatWindows.Remove(session);
            window.SuppressDock();
            window.Close();
        }

        RebuildDockedSessions();
        SelectedSession = session;
        StatusMessage = $"已收回会话: {TitleOf(session)}";
    }

    private void RebuildDockedSessions()
    {
        var desired = Sessions.Where(s => !_floatWindows.ContainsKey(s)).ToList();

        for (var i = DockedSessions.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(DockedSessions[i]))
                DockedSessions.RemoveAt(i);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            var current = DockedSessions.IndexOf(item);
            if (current < 0)
                DockedSessions.Insert(i, item);
            else if (current != i)
                DockedSessions.Move(current, i);
        }

        OnPropertyChanged(nameof(HasDockedSessions));
    }

    private void EnsureDockedSelection(int? preferredIndex = null)
    {
        if (SelectedSession is not null && DockedSessions.Contains(SelectedSession))
            return;

        if (DockedSessions.Count == 0)
        {
            SelectedSession = null;
            return;
        }

        var index = Math.Clamp(preferredIndex ?? 0, 0, DockedSessions.Count - 1);
        var fallback = DockedSessions[index];
        SelectedSession = fallback;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || !dispatcher.CheckAccess())
            return;

        dispatcher.BeginInvoke(new Action(() =>
        {
            if (SelectedSession is not null && DockedSessions.Contains(SelectedSession))
                return;
            SelectedSession = DockedSessions.Contains(fallback) ? fallback : DockedSessions.FirstOrDefault();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static string TitleOf(object session) => (session as ISshTab)?.Title ?? string.Empty;

    private void CloseSession(object? session)
    {
        if (session == null) return;

        var name = TitleOf(session);
        var extra = session is TerminalSessionViewModel or LocalTerminalSessionViewModel ? "终端连接将断开。" : string.Empty;
        if (!_dialogs.Confirm($"确定关闭会话「{name}」？{extra}", "关闭会话", warning: false))
            return;

        var index = Sessions.IndexOf(session);
        var wasSelected = ReferenceEquals(SelectedSession, session) || SelectedSession == session;

        // 若该会话正处于独立窗口，先关闭该窗口（避免触发"收回"）
        if (_floatWindows.TryGetValue(session, out var floatWin))
        {
            _floatWindows.Remove(session);
            floatWin.SuppressDock();
            floatWin.Close();
        }

        if (session is TerminalSessionViewModel term)
            _ = term.DisposeAsync();
        else if (session is LocalTerminalSessionViewModel local)
        {
            AiTerminals.CloseTerminal(local);
            _ = local.DisposeAsync();
        }
        Sessions.Remove(session);
        RebuildDockedSessions();
        EnsureDockedSelection(wasSelected ? index : null);

        StatusMessage = Sessions.Count == 0 ? "已关闭全部会话" : $"已关闭会话: {name}";
    }

    public void ReloadConfig() => LoadServers();

    private void LoadServers()
    {
        IsLoadingServers = true;
        ServersErrorMessage = string.Empty;
        AsyncOperation.Run("加载服务器", async () =>
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            Servers.Clear();
            foreach (var server in config.Servers)
                Servers.Add(server);

            DatasourceCount = config.DataSources.Length;
            ApplicationCount = config.Applications.Length;
            PortForwardCount = config.PortForwards.Length;
            RemoteDesktopCount = config.RemoteDesktops.Length;
            SyncTaskCount = config.SyncTasks.Length;
            SyncTaskEnabledCount = config.SyncTasks.Count(t => t.Enabled);
            TriggerCount = config.Triggers.Length;
            TriggerEnabledCount = config.Triggers.Count(t => t.Enabled);
            PortForwardRunningCount = _portForwardService.ActiveIds.Count;
            OnPropertyChanged(nameof(ServerCount));
            OnPropertyChanged(nameof(DisabledServerCount));
            OnPropertyChanged(nameof(HasDisabledServers));

            StatusMessage = $"已加载 {Servers.Count} 台服务器";
        }
        catch (Exception ex)
        {
            StatusMessage = $"错误: {ex.Message}";
            ServersErrorMessage = ex.Message;
        }
        finally
        {
            IsLoadingServers = false;
        }
    });
    }

    private void AddServer()
    {
        var editWindow = _windows.CreateServerEditWindow(Application.Current.MainWindow);
        var editViewModel = new ServerEditViewModel(_configService, _sshService, editWindow);
        editWindow.DataContext = editViewModel;

        editViewModel.DialogClosed += (_, result) =>
        {
            editWindow.Close();
            if (result) LoadServers();
        };

        editWindow.ShowDialog();
    }

    // 资产拓扑：独立窗口（不设 Owner / 不置顶，避免始终浮在主界面之上；可与主界面同时操作）
    private TopologyWindow? _topologyWindow;

    private void OpenTopology()
    {
        if (_topologyWindow is { IsLoaded: true })
        {
            _topologyWindow.Activate();
            return;
        }

        _topologyWindow = _windows.CreateTopologyWindow();
        _topologyWindow.Closed += (_, _) => _topologyWindow = null;
        _topologyWindow.Show();
    }

    // 审计日志：独立窗口（不设 Owner / 不置顶，避免始终浮在主界面之上；可与主界面同时操作）
    private AuditWindow? _auditWindow;

    private void OpenAudit()
    {
        if (_auditWindow is { IsLoaded: true })
        {
            _auditWindow.Activate();
            return;
        }

        _auditWindow = _windows.CreateAuditWindow();
        _auditWindow.Closed += (_, _) => _auditWindow = null;
        _auditWindow.Show();
    }

    // 批量执行：多服务器同时下发同一条命令
    private BatchExecWindow? _batchExecWindow;

    private void OpenBatchExec()
    {
        if (_batchExecWindow is { IsLoaded: true })
        {
            _batchExecWindow.Activate();
            return;
        }

        var vm = new BatchExecViewModel(Servers, _sshService);
        _batchExecWindow = _windows.CreateBatchExecWindow(vm);
        _batchExecWindow.Closed += (_, _) => _batchExecWindow = null;
        _batchExecWindow.Show();
    }

    // 资源监控：某台服务器的 CPU/内存/磁盘小面板
    private ServerMonitorWindow? _monitorWindow;

    private void OpenMonitor(SshServerConfig? server)
    {
        if (server == null)
            return;
        if (_monitorWindow is { IsLoaded: true })
        {
            _monitorWindow.Activate();
            return;
        }

        _monitorWindow = _windows.CreateServerMonitorWindow(new ServerMonitorViewModel(server, _sshService));
        _monitorWindow.Closed += (_, _) => _monitorWindow = null;
        _monitorWindow.Show();
    }

    // 会话日志：查看/回放终端会话记录
    private TerminalLogWindow? _logWindow;

    private void OpenTerminalLogs()
    {
        if (_logWindow is { IsLoaded: true })
        {
            _logWindow.Activate();
            return;
        }

        _logWindow = _windows.CreateTerminalLogWindow();
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show();
    }

    // 端口转发：本地/远程/动态 SOCKS
    private PortForwardWindow? _portForwardWindow;

    private void OpenPortForward()
    {
        if (_portForwardWindow is { IsLoaded: true })
        {
            _portForwardWindow.Activate();
            return;
        }

        var vm = new PortForwardViewModel(_configService, _portForwardService, Servers);
        _portForwardWindow = _windows.CreatePortForwardWindow(vm);
        _portForwardWindow.Closed += (_, _) =>
        {
            _portForwardWindow = null;
            _ = RefreshHomeCountsAsync(); // 规则可能增删/改
        };
        _portForwardWindow.Show();
    }

    // ---- 代理浏览器（经 SSH 动态转发 SOCKS，浏览器全部流量走该服务器出口） ----
    private readonly Dictionary<string, (Process Browser, string BaseDir)> _proxyBrowsers = new();

    private void OpenProxiedBrowser(SshServerConfig? server) => AsyncOperation.Run("打开代理浏览器", async () =>
    {
        if (server is null)
            return;
        if (server.Disabled)
        {
            StatusMessage = $"服务器 {server.Name} 已禁用，无法建立代理。";
            return;
        }

        var browser = ProxiedBrowserLauncher.FindChromiumBrowser();
        if (browser is null)
        {
            _dialogs.Warn(
                "未找到 Chromium 系浏览器（Edge / Chrome / Brave / Chromium）。\n请安装其中之一后重试。",
                "打开代理浏览器");
            return;
        }

        var port = ProxiedBrowserLauncher.GetFreeLoopbackPort();
        var forwardId = Guid.NewGuid().ToString("N");
        var config = new PortForwardConfig
        {
            Id = forwardId,
            Name = $"代理浏览器 · {server.Name}",
            ServerId = server.Id,
            Type = PortForwardType.Dynamic,
            BindHost = "127.0.0.1",
            BindPort = port,
            AutoStart = false
        };

        StatusMessage = $"正在为 {server.Name} 建立 SOCKS 代理…";
        var (ok, message) = await _portForwardService.StartAsync(server, config);
        if (!ok)
        {
            StatusMessage = "启动 SOCKS 代理失败";
            _dialogs.Error("启动 SOCKS 代理失败：\n\n" + message, "代理浏览器");
            return;
        }

        try
        {
            var launched = ProxiedBrowserLauncher.Launch(browser, port, server.Name);
            _proxyBrowsers[forwardId] = (launched.Process, launched.BaseDir);
            launched.Process.EnableRaisingEvents = true;
            launched.Process.Exited += (_, _) =>
                AsyncOperation.Run("清理代理浏览器", () => StopProxiedBrowserAsync(forwardId));
            StatusMessage = $"已打开代理浏览器：{server.Name}（SOCKS 127.0.0.1:{port}）";
        }
        catch (Exception ex)
        {
            await _portForwardService.StopAsync(forwardId);
            StatusMessage = "打开浏览器失败: " + ex.Message;
            _dialogs.Error("打开浏览器失败：\n\n" + ex.Message, "代理浏览器");
        }
    });

    private async Task StopProxiedBrowserAsync(string forwardId)
    {
        if (_proxyBrowsers.Remove(forwardId, out var entry))
            ProxiedBrowserLauncher.TryCleanup(entry.BaseDir);
        try { await _portForwardService.StopAsync(forwardId); } catch { /* ignore */ }
    }

    // 文件/文件夹同步（单向）
    private SyncWindow? _syncWindow;
    private void OpenSync()
    {
        if (_syncWindow is { IsLoaded: true })
        {
            _syncWindow.Activate();
            return;
        }

        _syncWindow = _windows.CreateSyncWindow();
        _syncWindow.Closed += (_, _) =>
        {
            _syncWindow = null;
            _ = RefreshHomeCountsAsync(); // 同步任务可能增删/改启停
        };
        _syncWindow.Show();
    }

    // 触发事件（手动 / 定时 / 同步完成后）
    private TriggerWindow? _triggerWindow;

    private void OpenTrigger()
    {
        if (_triggerWindow is { IsLoaded: true })
        {
            _triggerWindow.Activate();
            return;
        }

        _triggerWindow = _windows.CreateTriggerWindow();
        _triggerWindow.Closed += (_, _) =>
        {
            _triggerWindow = null;
            _ = RefreshHomeCountsAsync(); // 触发事件可能增删/改启停
        };
        _triggerWindow.Show();
    }

    /// <summary>刷新主页看板计数（配置类计数 + 端口转发运行数）。</summary>
    private async Task RefreshHomeCountsAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            DatasourceCount = config.DataSources.Length;
            ApplicationCount = config.Applications.Length;
            RemoteDesktopCount = config.RemoteDesktops.Length;
            PortForwardCount = config.PortForwards.Length;
            SyncTaskCount = config.SyncTasks.Length;
            SyncTaskEnabledCount = config.SyncTasks.Count(t => t.Enabled);
            TriggerCount = config.Triggers.Length;
            TriggerEnabledCount = config.Triggers.Count(t => t.Enabled);
        }
        catch
        {
            // 读取失败保持原值
        }
        PortForwardRunningCount = _portForwardService.ActiveIds.Count;
    }

    // SFTP 文件管理：本地+远程双栏 + 传输队列（每台服务器一个独立窗口）
    private readonly Dictionary<string, SftpManagerWindow> _sftpWindows = new();

    private void OpenSftpManager(SshServerConfig? server)
    {
        if (server == null)
            return;
        if (_sftpWindows.TryGetValue(server.Id, out var existing) && existing.IsLoaded)
        {
            existing.Activate();
            return;
        }

        var window = _windows.CreateSftpManagerWindow(new SftpManagerViewModel(server, _sshService));
        window.Closed += (_, _) => _sftpWindows.Remove(server.Id);
        _sftpWindows[server.Id] = window;
        window.Show();
    }

    // 远程互传：服务器 ↔ 服务器（源机直连推送到目标；每台源服务器一个独立非模态窗口）
    private readonly Dictionary<string, RemoteCopyWindow> _remoteCopyWindows = new();

    private void OpenRemoteCopy(SshServerConfig? server)
    {
        if (server == null)
            return;
        if (_remoteCopyWindows.TryGetValue(server.Id, out var existing) && existing.IsLoaded)
        {
            existing.Activate();
            return;
        }

        var window = _windows.CreateRemoteCopyWindow(server);
        window.Closed += (_, _) => _remoteCopyWindows.Remove(server.Id);
        _remoteCopyWindows[server.Id] = window;
        window.Show();
    }

    // 设置：整合「安全设置 / 工具分组 / 导入导出 / MCP 工具说明」为多 Tab 独立窗口
    private AppSettingsWindow? _settingsWindow;

    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = _windows.CreateAppSettingsWindow(_configService);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            LoadServers();
        };
        _settingsWindow.Show();
    }

    // 数据源管理：主区域页面
    private DatasourceManageViewModel? _datasourcePage;
    public DatasourceManageViewModel? DatasourcePage
    {
        get => _datasourcePage;
        private set { _datasourcePage = value; OnPropertyChanged(); }
    }

    private void NavigateDataSources()
    {
        DatasourcePage ??= CreateDatasourcePage();
        DatasourcePage.RefreshCommand.Execute(null);
        CurrentPage = MainPage.DataSources;
    }

    // 应用管理：主区域页面
    private ApplicationManageViewModel? _applicationPage;
    public ApplicationManageViewModel? ApplicationPage
    {
        get => _applicationPage;
        private set { _applicationPage = value; OnPropertyChanged(); }
    }

    private void NavigateApplications()
    {
        ApplicationPage ??= new ApplicationManageViewModel(_configService, Application.Current.MainWindow);
        ApplicationPage.RefreshCommand.Execute(null);
        CurrentPage = MainPage.Applications;
    }

    // 远程桌面（RDP / VNC）：主区域页面
    private RemoteDesktopManageViewModel? _remoteDesktopPage;
    public RemoteDesktopManageViewModel? RemoteDesktopPage
    {
        get => _remoteDesktopPage;
        private set { _remoteDesktopPage = value; OnPropertyChanged(); }
    }

    private void NavigateRemoteDesktops()
    {
        RemoteDesktopPage ??= new RemoteDesktopManageViewModel(_configService, services: _services);
        RemoteDesktopPage.RefreshCommand.Execute(null);
        CurrentPage = MainPage.RemoteDesktops;
    }

    private void CloseSessionTerminal(long sessionId)
    {
        void Close()
        {
            AiTerminals.CloseSession(sessionId.ToString());
            if (!_aiTerminals.Remove(sessionId.ToString(), out var vm))
                return;
            _ = vm.DisposeAsync();
            if (Sessions.Contains(vm))
            {
                var index = DockedSessions.IndexOf(vm);
                var wasSelected = ReferenceEquals(SelectedSession, vm);
                Sessions.Remove(vm);
                RebuildDockedSessions();
                EnsureDockedSelection(wasSelected ? index : null);
            }
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.Invoke(Close);
        else
            Close();
    }

    private void OpenAgent() => NavigateAgent();

    // 主页「AI 快捷提问」：把文字与附件填入 AI 运维助手的输入区
    private void AskAgent() => SendHomeAsk();

    /// <summary>把主页快捷提问的文本/附件送入 AI 运维助手输入区（助手已有内容时先确认覆盖）。</summary>
    public void SendHomeAsk()
    {
        var text = HomeQuestion?.Trim() ?? string.Empty;
        var paths = HomeAttachments.Select(a => a.Path).ToList();
        if (text.Length == 0 && paths.Count == 0)
        {
            HomeStatusIsError = true;
            HomeStatusMessage = "请先输入问题或添加附件";
            return;
        }

        var view = EnsureAgentPage();
        CurrentPage = MainPage.Agent;
        if (IsAgentDetached)
            ActivateAgent();
        var vm = view.ViewModel;
        if (!string.IsNullOrWhiteSpace(vm.Input) || vm.HasAttachments)
        {
            if (!_dialogs.Confirm(
                    "AI 运维助手的输入区已有内容，是否覆盖？",
                    "AI 快捷提问", warning: false))
                return;

            vm.ClearAttachments();
            vm.Input = string.Empty;
        }

        view.SubmitPrompt(text, paths);
        HomeQuestion = string.Empty;
        ClearHomeAttachments();
        HomeStatusIsError = false;
        HomeStatusMessage = "已填入 AI 助手输入区，请在助手窗口确认发送";
    }

    // ---- 主页快捷提问的附件 ----

    public ObservableCollection<HomeAttachment> HomeAttachments { get; } = new();

    public bool HasHomeAttachments => HomeAttachments.Count > 0;

    public void AddHomeAttachmentPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!System.IO.File.Exists(path))
                continue;
            if (HomeAttachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            var isImage = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";
            HomeAttachments.Add(new HomeAttachment
            {
                Name = System.IO.Path.GetFileName(path),
                Path = path,
                IsImage = isImage
            });
        }
        OnPropertyChanged(nameof(HasHomeAttachments));
    }

    public void ClearHomeAttachments()
    {
        if (HomeAttachments.Count == 0)
            return;
        HomeAttachments.Clear();
        OnPropertyChanged(nameof(HasHomeAttachments));
    }

    // 采集服务器快照(同步阻塞, 典型 10~30 秒)。单飞: 服务内 per-server 锁 + 库内 Running 唯一约束跨进程生效;
    // UI 再加一道全局忙标志避免同一窗口重复点击。完成后打开历史窗口并定位到本次快照。
    private void RefreshSnapshot(SshServerConfig? server) => AsyncOperation.Run("采集服务器快照", async () =>
    {
        if (server == null) return;
        if (server.Disabled)
        {
            StatusMessage = $"服务器 {server.Name} 已禁用, 不允许采集快照。";
            return;
        }
        if (_snapshotBusy)
        {
            StatusMessage = "已有快照采集任务在进行中, 请稍候。";
            return;
        }

        _snapshotBusy = true;
        (SnapshotRefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
        StatusMessage = $"正在采集 {server.Name} 的快照…(典型 10~30 秒, 弱网更久)";
        long? snapshotId = null;
        try
        {
            var config = await _configService.LoadConfigAsync();
            var result = await _snapshotService.RefreshAsync(server, config.Snapshot ?? new SnapshotConfig(), CancellationToken.None);
            snapshotId = result.SnapshotId;
            StatusMessage = result.Status switch
            {
                "succeeded" => $"快照采集完成: {server.Name} (#{result.SnapshotId}, {result.Snapshot?.DurationMs:F0}ms)",
                "snapshot_in_progress" => result.Error ?? "该服务器已有快照在采集中",
                _ => $"快照采集失败: {result.Error}"
            };
        }
        catch (Exception ex)
        {
            StatusMessage = $"快照采集异常: {ex.Message}";
        }
        finally
        {
            _snapshotBusy = false;
            (SnapshotRefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        OpenSnapshotHistory(server, snapshotId);
    });

    // 历史快照窗口: 可切换服务器, 列表→点详情; 直接复用同一查看页面。
    private void OpenSnapshotHistory(SshServerConfig? server, long? initialSnapshotId = null)
    {
        var window = _windows.CreateSnapshotHistoryWindow(
            _snapshotStore, _configService, server?.Id, initialSnapshotId, Application.Current.MainWindow);
        window.ShowDialog();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>主窗口右侧主区域可切换的功能页面。</summary>
public enum MainPage
{
    Home,
    Servers,
    DataSources,
    Applications,
    RemoteDesktops,
    Agent
}

/// <summary>SSH 会话管理窗口的布局方式。</summary>
public enum SessionLayout
{
    Tabs,
    Tile1,
    Tile2,
    Tile3
}

/// <summary>主页「AI 快捷提问」的待转交附件。</summary>
public sealed class HomeAttachment
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool IsImage { get; init; }
}

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
