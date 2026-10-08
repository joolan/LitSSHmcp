using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Snapshot;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Win32;

namespace LitSSHmcp.App.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService;
    private readonly ISshService _sshService;
    private readonly IAuditLogService _auditLogService;
    private readonly ITopologyStore _topologyStore = new TopologyStore();
    private readonly PortForwardService _portForwardService = new();

    // 快照: 采集服务实例须长期复用(内存单飞锁绑定实例); 库供历史窗口读取。
    private readonly ISnapshotService _snapshotService = AppServiceFactory.CreateSnapshotService();
    private readonly ISnapshotStore _snapshotStore = AppServiceFactory.CreateSnapshotStore();
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
    public ICommand ConnectCommand { get; }
    public ICommand CloseSessionCommand { get; }
    public ICommand OpenAuditCommand { get; }
    public ICommand OpenTopologyCommand { get; }
    public ICommand OpenAgentCommand { get; }
    public ICommand NavigateHomeCommand { get; }
    public ICommand NavigateServersCommand { get; }
    public ICommand NavigateDataSourcesCommand { get; }
    public ICommand NavigateApplicationsCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenSessionManagerCommand { get; }
    public ICommand OpenTerminalCommand { get; }
    public ICommand OpenBatchExecCommand { get; }
    public ICommand OpenMonitorCommand { get; }
    public ICommand OpenTerminalLogsCommand { get; }
    public ICommand OpenPortForwardCommand { get; }
    public ICommand OpenSyncCommand { get; }
    public ICommand OpenSftpManagerCommand { get; }
    public ICommand OpenRemoteCopyCommand { get; }
    public ICommand AskAgentCommand { get; }
    public ICommand SnapshotRefreshCommand { get; }
    public ICommand OpenSnapshotHistoryCommand { get; }

    public MainViewModel()
    {
        _configService = new ConfigService();
        _sshService = new SshService();
        _auditLogService = new AuditLogService();

        LoadServersCommand = new RelayCommand(_ => LoadServers());
        AddServerCommand = new RelayCommand(_ => AddServer());
        EditServerCommand = new RelayCommand(_ => EditServer(), _ => SelectedServer != null);
        DeleteServerCommand = new RelayCommand(_ => DeleteServer(), _ => SelectedServer != null);
        ConnectCommand = new RelayCommand(_ => Connect(SelectedServer), _ => SelectedServer != null);
        CloseSessionCommand = new RelayCommand(p => CloseSession(p));
        OpenAuditCommand = new RelayCommand(_ => OpenAudit());
        OpenTopologyCommand = new RelayCommand(_ => OpenTopology());
        OpenAgentCommand = new RelayCommand(_ => OpenAgent());
        NavigateHomeCommand = new RelayCommand(_ => CurrentPage = MainPage.Home);
        NavigateServersCommand = new RelayCommand(_ => CurrentPage = MainPage.Servers);
        NavigateDataSourcesCommand = new RelayCommand(_ => NavigateDataSources());
        NavigateApplicationsCommand = new RelayCommand(_ => NavigateApplications());
        OpenSettingsCommand = new RelayCommand(_ => OpenSettings());
        OpenSessionManagerCommand = new RelayCommand(_ => ShowSessionWindow());
        OpenTerminalCommand = new RelayCommand(_ => ConnectTerminal(SelectedServer), _ => SelectedServer != null);
        OpenBatchExecCommand = new RelayCommand(_ => OpenBatchExec());
        OpenMonitorCommand = new RelayCommand(_ => OpenMonitor(SelectedServer), _ => SelectedServer != null);
        OpenTerminalLogsCommand = new RelayCommand(_ => OpenTerminalLogs());
        OpenPortForwardCommand = new RelayCommand(_ => OpenPortForward());
        OpenSyncCommand = new RelayCommand(_ => OpenSync());
        OpenSftpManagerCommand = new RelayCommand(_ => OpenSftpManager(SelectedServer), _ => SelectedServer != null);
        OpenRemoteCopyCommand = new RelayCommand(_ => OpenRemoteCopy(SelectedServer), _ => SelectedServer != null);
        AskAgentCommand = new RelayCommand(_ => AskAgent());
        SnapshotRefreshCommand = new RelayCommand(_ => RefreshSnapshot(SelectedServer), _ => SelectedServer != null && !_snapshotBusy);
        OpenSnapshotHistoryCommand = new RelayCommand(_ => OpenSnapshotHistory(SelectedServer), _ => SelectedServer != null);

        Sessions.CollectionChanged += (_, _) =>
        {
            RebuildDockedSessions();
            OnPropertyChanged(nameof(HasSessions));
        };

        // 服务器列表按「分组」字段分组（连接管理器树形分组）
        GroupedServers = System.Windows.Data.CollectionViewSource.GetDefaultView(Servers);
        GroupedServers.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(SshServerConfig.Group)));

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
            (EditServerCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteServerCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenTerminalCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenMonitorCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenSftpManagerCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenRemoteCopyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ConnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SnapshotRefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenSnapshotHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

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

    private string _homeQuestion = string.Empty;

    /// <summary>主页「AI 快捷提问」输入内容。</summary>
    public string HomeQuestion
    {
        get => _homeQuestion;
        set { _homeQuestion = value; OnPropertyChanged(); }
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

    public void Connect(SshServerConfig? server)
    {
        if (server == null) return;

        if (server.Disabled)
        {
            StatusMessage = $"服务器 {server.Name} 已禁用, 不允许连接。请先「编辑」并取消勾选\"禁用\"。";
            return;
        }

        var existing = Sessions.OfType<SessionViewModel>().FirstOrDefault(s => s.Server.Id == server.Id);
        if (existing == null)
        {
            existing = new SessionViewModel(server, _sshService, _auditLogService);
            Sessions.Add(existing);
            StatusMessage = $"已打开会话: {server.Name}";
        }

        SelectedSession = existing;
        ShowSessionWindow();
    }

    /// <summary>打开一个交互式终端标签（PTY shell）。</summary>
    public void ConnectTerminal(SshServerConfig? server)
    {
        if (server == null) return;

        if (server.Disabled)
        {
            StatusMessage = $"服务器 {server.Name} 已禁用, 不允许连接。";
            return;
        }

        var existing = Sessions.OfType<TerminalSessionViewModel>().FirstOrDefault(t => t.Server.Id == server.Id);
        if (existing == null)
        {
            existing = new TerminalSessionViewModel(server, _sshService);
            Sessions.Add(existing);
            StatusMessage = $"已打开终端: {server.Name}";
        }

        SelectedSession = existing;
        ShowSessionWindow();
    }

    // SSH 会话管理窗口（独立非模态窗口：多标签，每个服务器一个标签；不设 Owner/不置顶）
    private SshSessionWindow? _sessionWindow;

    private void ShowSessionWindow()
    {
        if (_sessionWindow is { IsLoaded: true })
        {
            _sessionWindow.Activate();
            return;
        }

        _sessionWindow = new SshSessionWindow { DataContext = this, Topmost = false };
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
            Sessions.Clear();
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

        var window = new SshSessionFloatWindow(this, session) { Topmost = false };
        window.DockRequested += DockSession;
        window.CloseRequested += CloseSession;
        _floatWindows[session] = window;
        RebuildDockedSessions();

        if (ReferenceEquals(SelectedSession, session))
            SelectedSession = DockedSessions.FirstOrDefault();

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
        DockedSessions.Clear();
        foreach (var s in Sessions)
        {
            if (!_floatWindows.ContainsKey(s))
                DockedSessions.Add(s);
        }
        OnPropertyChanged(nameof(HasDockedSessions));
    }

    private static string TitleOf(object session) => session switch
    {
        SessionViewModel s => s.Server.Name,
        TerminalSessionViewModel t => t.Server.Name,
        _ => string.Empty
    };

    private void CloseSession(object? session)
    {
        if (session == null) return;

        var name = session switch
        {
            SessionViewModel s => s.Server.Name,
            TerminalSessionViewModel t => t.Server.Name,
            _ => string.Empty
        };
        var extra = session is TerminalSessionViewModel ? "终端连接将断开。" : string.Empty;
        var confirm = MessageBox.Show($"确定关闭会话「{name}」？{extra}",
            "关闭会话", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        var index = Sessions.IndexOf(session);

        // 若该会话正处于独立窗口，先关闭该窗口（避免触发"收回"）
        if (_floatWindows.TryGetValue(session, out var floatWin))
        {
            _floatWindows.Remove(session);
            floatWin.SuppressDock();
            floatWin.Close();
        }

        if (session is TerminalSessionViewModel term)
            _ = term.DisposeAsync();
        Sessions.Remove(session);
        RebuildDockedSessions();
        if (ReferenceEquals(SelectedSession, session) || SelectedSession == session)
            SelectedSession = DockedSessions.Count > 0 ? DockedSessions[Math.Max(0, Math.Min(index, DockedSessions.Count - 1))] : null;

        StatusMessage = Sessions.Count == 0 ? "已关闭全部会话" : $"已关闭会话: {name}";
    }

    private async void LoadServers()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            Servers.Clear();
            foreach (var server in config.Servers)
                Servers.Add(server);

            DatasourceCount = config.DataSources.Length;
            ApplicationCount = config.Applications.Length;
            OnPropertyChanged(nameof(ServerCount));
            OnPropertyChanged(nameof(DisabledServerCount));

            StatusMessage = $"已加载 {Servers.Count} 台服务器";
        }
        catch (Exception ex)
        {
            StatusMessage = $"错误: {ex.Message}";
        }
    }

    private void AddServer()
    {
        var editWindow = new ServerEditWindow { Owner = Application.Current.MainWindow };
        var editViewModel = new ServerEditViewModel(_configService, _sshService, editWindow);
        editWindow.DataContext = editViewModel;

        editViewModel.DialogClosed += (_, result) =>
        {
            editWindow.Close();
            if (result) LoadServers();
        };

        editWindow.ShowDialog();
    }

    private void EditServer()
    {
        if (SelectedServer == null) return;

        var editWindow = new ServerEditWindow { Owner = Application.Current.MainWindow };
        var editViewModel = new ServerEditViewModel(_configService, _sshService, editWindow, SelectedServer);
        editWindow.DataContext = editViewModel;

        editViewModel.DialogClosed += (_, result) =>
        {
            editWindow.Close();
            if (result) LoadServers();
        };

        editWindow.ShowDialog();
    }

    private async void DeleteServer()
    {
        if (SelectedServer == null) return;

        try
        {
            var config = await _configService.LoadConfigAsync();
            var nodeId = AssetNode.Ssh(SelectedServer.Id);
            var references = RelationHelper.Referencing(config, nodeId);

            var message = $"确定要删除服务器 \"{SelectedServer.Name}\" 吗？";
            message += references.Length > 0
                ? $"\n\n存在 {references.Length} 条关系记录引用它，将一并删除：\n{RelationHelper.Describe(references)}"
                : "\n\n（未发现引用它的关系记录）";

            if (MessageBox.Show(message, "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            config.Servers = config.Servers.Where(s => s.Id != SelectedServer.Id).ToArray();
            config.Relations = config.Relations.Where(r => r.From != nodeId && r.To != nodeId).ToArray();
            await _configService.SaveConfigAsync(config);
            await CleanupDiscoveredEdgesAsync(nodeId);

            StatusMessage = $"已删除服务器: {SelectedServer.Name}";
            SelectedServer = null;
            LoadServers();
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败: {ex.Message}";
        }
    }

    private async Task CleanupDiscoveredEdgesAsync(string nodeId)
    {
        try
        {
            await _topologyStore.InitializeAsync();
            await _topologyStore.RemoveEdgesByNodeAsync(nodeId);
        }
        catch
        {
            // 清理自动发现边失败不阻断删除
        }
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

        _topologyWindow = new TopologyWindow { Topmost = false };
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

        _auditWindow = new AuditWindow { Topmost = false };
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
        _batchExecWindow = new BatchExecWindow { DataContext = vm, Topmost = false };
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

        _monitorWindow = new ServerMonitorWindow(new ServerMonitorViewModel(server, _sshService)) { Topmost = false };
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

        _logWindow = new TerminalLogWindow { Topmost = false };
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
        _portForwardWindow = new PortForwardWindow(vm) { Topmost = false };
        _portForwardWindow.Closed += (_, _) => _portForwardWindow = null;
        _portForwardWindow.Show();
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

        _syncWindow = new SyncWindow { Topmost = false };
        _syncWindow.Closed += (_, _) => _syncWindow = null;
        _syncWindow.Show();
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

        var window = new SftpManagerWindow(new SftpManagerViewModel(server, _sshService)) { Topmost = false };
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

        var window = new RemoteCopyWindow(server) { Topmost = false };
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

        _settingsWindow = new AppSettingsWindow(_configService);
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
        DatasourcePage ??= new DatasourceManageViewModel(_configService);
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

    // AI 运维助手(内嵌 MCP 客户端 + OpenAI 兼容大模型; 需先在「AI 助手设置」配置模型)
    // 非模态独立窗口: 可与主界面同时操作
    private AgentWindow? _agentWindow;

    private AgentWindow EnsureAgentWindow()
    {
        if (_agentWindow is { IsLoaded: true })
        {
            _agentWindow.Activate();
            return _agentWindow;
        }

        _agentWindow = new AgentWindow(_configService, AppServiceFactory.CreateAgentContextStore(), AppServiceFactory.BundledSkillsDir) { Topmost = false };
        _agentWindow.Closed += (_, _) => _agentWindow = null;
        _agentWindow.Show();
        return _agentWindow;
    }

    private void OpenAgent() => EnsureAgentWindow();

    // 主页「AI 快捷提问」：把文字与附件填入 AI 运维助手的输入区
    private void AskAgent() => SendHomeAsk();

    /// <summary>把主页快捷提问的文本/附件送入 AI 运维助手输入区（助手已有内容时先确认覆盖）。</summary>
    public void SendHomeAsk()
    {
        var text = HomeQuestion?.Trim() ?? string.Empty;
        var paths = HomeAttachments.Select(a => a.Path).ToList();
        if (text.Length == 0 && paths.Count == 0)
        {
            StatusMessage = "请先输入问题或添加附件";
            return;
        }

        var window = EnsureAgentWindow();
        var vm = window.ViewModel;
        if (!string.IsNullOrWhiteSpace(vm.Input) || vm.HasAttachments)
        {
            var result = MessageBox.Show(
                "AI 运维助手的输入区已有内容，是否覆盖？",
                "AI 快捷提问", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;

            vm.ClearAttachments();
            vm.Input = string.Empty;
        }

        window.SubmitPrompt(text, paths);
        HomeQuestion = string.Empty;
        ClearHomeAttachments();
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
    private async void RefreshSnapshot(SshServerConfig? server)
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
    }

    // 历史快照窗口: 可切换服务器, 列表→点详情; 直接复用同一查看页面。
    private void OpenSnapshotHistory(SshServerConfig? server, long? initialSnapshotId = null)
    {
        var window = new SnapshotHistoryWindow(_snapshotStore, _configService, server?.Id, initialSnapshotId)
        {
            Owner = Application.Current.MainWindow
        };
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
    Applications
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
