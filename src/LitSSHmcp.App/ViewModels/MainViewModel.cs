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
    public ICommand OpenDatasourceManagerCommand { get; }
    public ICommand OpenTopologyManagerCommand { get; }
    public ICommand OpenApplicationManagerCommand { get; }
    public ICommand OpenAuditCommand { get; }
    public ICommand OpenSecuritySettingsCommand { get; }
    public ICommand OpenTopologyCommand { get; }
    public ICommand ExportConfigCommand { get; }
    public ICommand ImportConfigCommand { get; }
    public ICommand OpenMcpToolsCommand { get; }
    public ICommand OpenToolGroupsCommand { get; }

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
        OpenDatasourceManagerCommand = new RelayCommand(_ => OpenDatasourceManager());
        OpenTopologyManagerCommand = new RelayCommand(_ => OpenTopologyManager());
        OpenApplicationManagerCommand = new RelayCommand(_ => OpenApplicationManager());
        OpenAuditCommand = new RelayCommand(_ => OpenAudit());
        OpenSecuritySettingsCommand = new RelayCommand(_ => OpenSecuritySettings());
        OpenTopologyCommand = new RelayCommand(_ => OpenTopology());
        ExportConfigCommand = new RelayCommand(_ => ExportConfig());
        ImportConfigCommand = new RelayCommand(_ => ImportConfig());
        OpenMcpToolsCommand = new RelayCommand(_ => OpenMcpTools());
        OpenToolGroupsCommand = new RelayCommand(_ => OpenToolGroups());

        Sessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSessions));

        LoadServers();
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
        }
    }

    public SessionViewModel? SelectedSession
    {
        get => _selectedSession;
        set { _selectedSession = value; OnPropertyChanged(); }
    }

    public bool HasSessions => Sessions.Count > 0;

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

    private void OpenSecuritySettings() => new SecuritySettingsWindow { Owner = Application.Current.MainWindow }.ShowDialog();

    private void OpenTopology() => new TopologyWindow { Owner = Application.Current.MainWindow }.ShowDialog();

    private void OpenDatasourceManager() => new DatasourceManageWindow { Owner = Application.Current.MainWindow }.ShowDialog();

    private void OpenApplicationManager() => new ApplicationManageWindow { Owner = Application.Current.MainWindow }.ShowDialog();

    private void OpenTopologyManager() => new TopologyManageWindow { Owner = Application.Current.MainWindow }.ShowDialog();

    private void OpenAudit() => new AuditWindow { Owner = Application.Current.MainWindow }.ShowDialog();

    // MCP 工具说明(内容来自 docs/TOOLS.md 嵌入资源, 与 MCP 服务器端工具注解双向同步 — 见 McpToolsWindow 文件头注释)
    private void OpenMcpTools() => new McpToolsWindow { Owner = Application.Current.MainWindow }.ShowDialog();

    // 工具分组设置(写入 config.json 的 tools.enabledGroups)
    private void OpenToolGroups() => new ToolGroupsWindow { Owner = Application.Current.MainWindow }.ShowDialog();

    private async void ExportConfig()
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"litssh-config-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            Filter = "JSON 文件|*.json|所有文件|*.*"
        };

        if (dialog.ShowDialog() != true)
            return;

        var choice = MessageBox.Show(
            "导出内容:\n\n[是] 包含密钥（DPAPI 密文，仅本机当前用户可用）\n[否] 脱敏导出（不含任何密码）",
            "导出配置", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (choice == MessageBoxResult.Cancel)
            return;

        try
        {
            if (choice == MessageBoxResult.Yes)
            {
                File.Copy(_configService.GetConfigPath(), dialog.FileName, overwrite: true);
            }
            else
            {
                var config = await _configService.LoadConfigAsync();
                foreach (var server in config.Servers)
                {
                    server.Password = null;
                    server.KeyFilePassphrase = null;
                    server.SudoPassword = null;
                }
                foreach (var ds in config.DataSources)
                    ds.Password = null;

                var json = JsonSerializer.Serialize(config, AppConfigJson.Options);
                await File.WriteAllTextAsync(dialog.FileName, json);
            }

            StatusMessage = $"已导出配置: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"导出失败: {ex.Message}";
        }
    }

    private async void ImportConfig()
    {
        var dialog = new OpenFileDialog { Filter = "JSON 文件|*.json|所有文件|*.*" };
        if (dialog.ShowDialog() != true)
            return;

        if (MessageBox.Show("导入将覆盖当前配置（含服务器/数据源/应用/关系/安全设置），确定继续？",
                "导入配置", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            var json = await File.ReadAllTextAsync(dialog.FileName, Encoding.UTF8);
            var config = JsonSerializer.Deserialize<AppConfig>(json, AppConfigJson.Options);
            if (config == null)
            {
                StatusMessage = "导入失败: 文件内容不是有效的配置";
                return;
            }

            await _configService.SaveConfigAsync(config);
            StatusMessage = $"已导入配置: {dialog.FileName}";
            LoadServers();
        }
        catch (Exception ex)
        {
            StatusMessage = $"导入失败: {ex.Message}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
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
