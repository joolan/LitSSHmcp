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

    // 快照: 采集服务实例须长期复用(内存单飞锁绑定实例); 库供历史窗口读取。
    private readonly ISnapshotService _snapshotService = AppServiceFactory.CreateSnapshotService();
    private readonly ISnapshotStore _snapshotStore = AppServiceFactory.CreateSnapshotStore();
    private bool _snapshotBusy;

    private SshServerConfig? _selectedServer;
    private SessionViewModel? _selectedSession;
    private string _statusMessage = string.Empty;

    public ObservableCollection<SshServerConfig> Servers { get; } = new();
    public ObservableCollection<SessionViewModel> Sessions { get; } = new();

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
        CloseSessionCommand = new RelayCommand(p => CloseSession(p as SessionViewModel));
        OpenAuditCommand = new RelayCommand(_ => OpenAudit());
        OpenTopologyCommand = new RelayCommand(_ => OpenTopology());
        OpenAgentCommand = new RelayCommand(_ => OpenAgent());
        NavigateHomeCommand = new RelayCommand(_ => CurrentPage = MainPage.Home);
        NavigateServersCommand = new RelayCommand(_ => CurrentPage = MainPage.Servers);
        NavigateDataSourcesCommand = new RelayCommand(_ => NavigateDataSources());
        NavigateApplicationsCommand = new RelayCommand(_ => NavigateApplications());
        OpenSettingsCommand = new RelayCommand(_ => OpenSettings());
        SnapshotRefreshCommand = new RelayCommand(_ => RefreshSnapshot(SelectedServer), _ => SelectedServer != null && !_snapshotBusy);
        OpenSnapshotHistoryCommand = new RelayCommand(_ => OpenSnapshotHistory(SelectedServer), _ => SelectedServer != null);

        Sessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSessions));

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
            (ConnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SnapshotRefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenSnapshotHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public SessionViewModel? SelectedSession
    {
        get => _selectedSession;
        set { _selectedSession = value; OnPropertyChanged(); }
    }

    public bool HasSessions => Sessions.Count > 0;

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

    public void Connect(SshServerConfig? server)
    {
        if (server == null) return;

        if (server.Disabled)
        {
            StatusMessage = $"服务器 {server.Name} 已禁用, 不允许连接。请先「编辑」并取消勾选\"禁用\"。";
            return;
        }

        var existing = Sessions.FirstOrDefault(s => s.Server.Id == server.Id);
        if (existing == null)
        {
            existing = new SessionViewModel(server, _sshService, _auditLogService);
            Sessions.Add(existing);
            StatusMessage = $"已打开会话: {server.Name}";
        }

        SelectedSession = existing;
    }

    private void CloseSession(SessionViewModel? session)
    {
        if (session == null) return;
        var index = Sessions.IndexOf(session);
        Sessions.Remove(session);
        if (SelectedSession == session)
            SelectedSession = Sessions.Count > 0 ? Sessions[Math.Max(0, Math.Min(index, Sessions.Count - 1))] : null;

        StatusMessage = Sessions.Count == 0 ? "已关闭全部会话" : $"已关闭会话: {session.Server.Name}";
    }

    private async void LoadServers()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            Servers.Clear();
            foreach (var server in config.Servers)
                Servers.Add(server);

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

    private void OpenAgent()
    {
        if (_agentWindow is { IsLoaded: true })
        {
            _agentWindow.Activate();
            return;
        }

        _agentWindow = new AgentWindow(_configService, AppServiceFactory.CreateAgentContextStore(), AppServiceFactory.BundledSkillsDir);
        _agentWindow.Closed += (_, _) => _agentWindow = null;
        _agentWindow.Show();
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
