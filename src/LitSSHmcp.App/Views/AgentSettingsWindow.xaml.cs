using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LitSSHmcp.Agent;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Storage;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class AgentSettingsWindow : FluentWindow
{
    private readonly AgentSettingsViewModel _viewModel = new();

    public AgentSettingsWindow(IConfigService configService, string? initialTab = null)
    {
        InitializeComponent();
        DataContext = _viewModel;
        LitSSHmcp.App.Services.WindowLayout.Attach(this, "agent-settings");

        SelectTab(initialTab);

        // 切到「归档记录」时刷新列表
        Tabs.SelectionChanged += (_, _) =>
        {
            if (Tabs.SelectedItem is TabItem { Header: string header } && header == "归档记录")
                _ = _viewModel.LoadArchivedAsync();
        };

        // API Key 用 PasswordBox 展示为圆点：随选中模型同步，输入时回写 VM
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AgentSettingsViewModel.SelectedProvider))
                SyncApiKeyBox();
        };
        Loaded += (_, _) => SyncApiKeyBox();
    }

    private bool _syncingApiKey;

    /// <summary>按 Tab 键值（model/archive/agents/mcp…）定位到对应 Tab：按 Header 文本匹配（不依赖索引，避免插入新 Tab 后错位）。
    /// 窗口已打开被再次请求时也用它切换。</summary>
    public void SelectTab(string? tabKey)
    {
        if (string.IsNullOrWhiteSpace(tabKey))
            return;

        var target = tabKey.Trim().ToLowerInvariant() switch
        {
            "model" => "大模型设置",
            "archive" => "归档记录",
            "agents" or "agent" => "智能体",
            "mcp" or "mcpServers" => "MCP 服务器",
            _ => null
        };
        if (target is null)
            return;

        for (var i = 0; i < Tabs.Items.Count; i++)
        {
            if (Tabs.Items[i] is TabItem { Header: string header } &&
                string.Equals(header, target, StringComparison.Ordinal))
            {
                Tabs.SelectedIndex = i;
                break;
            }
        }
    }

    private void SyncApiKeyBox()
    {
        _syncingApiKey = true;
        ApiKeyBox.Password = _viewModel.SelectedProvider?.ApiKey ?? string.Empty;
        _syncingApiKey = false;
    }

    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingApiKey)
            return;
        if (_viewModel.SelectedProvider is { } provider)
            provider.ApiKey = ApiKeyBox.Password;
    }

    private void OnAdd(object sender, RoutedEventArgs e) => _viewModel.AddProvider();

    private void OnAddAgent(object sender, RoutedEventArgs e) => _viewModel.AddAgent();

    private void OnRemoveAgent(object sender, RoutedEventArgs e)
    {
        var agent = _viewModel.SelectedAgent;
        if (agent is null)
            return;
        var result = System.Windows.MessageBox.Show(
            $"确定删除智能体「{agent.Name}」？引用它的会话下次打开时回退到全局默认智能体。",
            "删除智能体", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result == System.Windows.MessageBoxResult.Yes)
            _viewModel.RemoveSelectedAgent();
    }

    private void OnAddModel(object sender, RoutedEventArgs e) => _viewModel.AddModel();

    private void OnAddMcp(object sender, RoutedEventArgs e) => _viewModel.AddMcpServer();

    private void OnValidateMcp(object sender, RoutedEventArgs e) => _ = _viewModel.ValidateMcpServerAsync(_viewModel.SelectedMcpServer);

    private void OnToggleMcp(object sender, RoutedEventArgs e)
    {
        var server = _viewModel.SelectedMcpServer;
        if (server is null)
            return;

        if (server.Enabled)
        {
            if (server.Builtin)
            {
                var result = System.Windows.MessageBox.Show(
                    "停用内置 LitSSH MCP 后，AI 将没有本机服务器/数据源等运维工具（只能用工作区文档、技能与外部 MCP）。确定停用？",
                    "停用内置 MCP", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
                if (result != System.Windows.MessageBoxResult.Yes)
                    return;
            }
            _viewModel.DisableMcpServer(server);
            return;
        }

        // 外部：启用前必须连接校验通过（VM 内完成，失败保持停用）
        _ = _viewModel.EnableMcpServerAsync(server);
    }

    private void OnRemoveMcp(object sender, RoutedEventArgs e)
    {
        var server = _viewModel.SelectedMcpServer;
        if (server is null || server.Builtin)
            return;
        var result = System.Windows.MessageBox.Show(
            $"确定删除外部 MCP「{server.Name}」？删除后其工具不再提供（点「保存」生效）。",
            "删除 MCP 服务器", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result == System.Windows.MessageBoxResult.Yes)
            _viewModel.RemoveSelectedMcpServer();
    }

    private void OnRemoveModel(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ModelItemEdit model)
            _viewModel.RemoveModel(model);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        var provider = _viewModel.SelectedProvider;
        if (provider is null)
            return;
        var result = System.Windows.MessageBox.Show(
            $"确定删除服务商「{provider.Name}」及其下 {provider.Models.Count} 个模型？删除后立即生效。",
            "删除服务商", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result == System.Windows.MessageBoxResult.Yes)
            _ = _viewModel.RemoveSelectedProviderAsync();
    }

    private void OnReload(object sender, RoutedEventArgs e) => _viewModel.Load();

    private void OnSave(object sender, RoutedEventArgs e) => _viewModel.Save();

    // 右键模型：选中该项（让右键菜单作用于该模型）
    private void OnProviderRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
            item.IsSelected = true;
    }

    private void OnToggleEnabled(object sender, RoutedEventArgs e) => _ = _viewModel.ToggleSelectedEnabledAsync();

    private void OnTestConnection(object sender, RoutedEventArgs e) => _ = _viewModel.TestSelectedProviderAsync();

    private void OnRefreshArchived(object sender, RoutedEventArgs e) => _ = _viewModel.LoadArchivedAsync();

    private void OnRestoreArchived(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AgentSessionRow row)
            _ = _viewModel.RestoreArchivedAsync(row);
    }

    private void OnDeleteArchived(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AgentSessionRow row)
            return;
        var result = System.Windows.MessageBox.Show(
            $"确定永久删除归档会话「{row.Title}」？此操作不可恢复。",
            "删除归档会话", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result == System.Windows.MessageBoxResult.Yes)
            _ = _viewModel.DeleteArchivedAsync(row);
    }
}
