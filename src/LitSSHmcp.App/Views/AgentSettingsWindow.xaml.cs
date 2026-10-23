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
    private readonly LitSSHmcp.App.Services.UserDialogService _dialogs = new();

    public AgentSettingsWindow(IConfigService configService, string? initialTab = null)
    {
        InitializeComponent();
        DataContext = _viewModel;
        LitSSHmcp.App.Services.WindowLayout.Attach(this, "agent-settings");

        SelectTab(initialTab);

        // 切到「归档记录」时刷新列表；切到「记忆管理」时刷新记忆明细
        Tabs.SelectionChanged += (_, _) =>
        {
            if (Tabs.SelectedItem is TabItem { Header: string header })
            {
                if (header == "归档记录")
                    _ = _viewModel.LoadArchivedAsync();
                else if (header == "记忆管理")
                    _ = _viewModel.LoadMemoryEntriesAsync();
            }
        };

        // API Key 用 PasswordBox 展示为圆点：随选中模型同步，输入时回写 VM
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AgentSettingsViewModel.SelectedProvider))
                SyncApiKeyBox();
            else if (e.PropertyName == nameof(AgentSettingsViewModel.MemoryApiKey))
                SyncMemoryApiKeyBox();   // 加载/重新加载后把 VM 值同步进圆点框
        };
        Loaded += (_, _) =>
        {
            SyncApiKeyBox();
            SyncMemoryApiKeyBox();
        };
    }

    private bool _syncingApiKey;
    private bool _syncingMemoryKey;

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
            "memory" or "memories" or "memoryManage" => "记忆管理",
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

    private void SyncMemoryApiKeyBox()
    {
        _syncingMemoryKey = true;
        MemoryApiKeyBox.Password = _viewModel.MemoryApiKey ?? string.Empty;
        _syncingMemoryKey = false;
    }

    private void OnMemoryApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingMemoryKey)
            return;
        _viewModel.MemoryApiKey = MemoryApiKeyBox.Password;
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

    private void OnTestEmbeddings(object sender, RoutedEventArgs e) => _ = _viewModel.TestEmbeddingsAsync();

    private void OnMemoryStats(object sender, RoutedEventArgs e) => _ = _viewModel.LoadMemoryStatsAsync();

    private void OnReloadMemories(object sender, RoutedEventArgs e) => _ = _viewModel.LoadMemoryEntriesAsync();

    private void OnDeleteMemory(object sender, RoutedEventArgs e)
    {
        var entry = _viewModel.SelectedMemoryEntry;
        if (entry is null)
        {
            System.Windows.MessageBox.Show("请先在列表中选中一条记忆，再点「删除选中」。", "删除记忆",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        var keyText = string.IsNullOrEmpty(entry.Key) ? "会话片段" : entry.Key;
        var preview = entry.Content.Length <= 80 ? entry.Content : entry.Content[..80] + "…";
        var result = System.Windows.MessageBox.Show(
            $"确定删除这条记忆？\n\n[{entry.Kind}:{keyText}]\n{preview}\n\n删除后 AI 不再召回该经验；历史快照仍保留，可由管理员接口恢复。",
            "删除记忆", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result == System.Windows.MessageBoxResult.Yes)
            _ = _viewModel.DeleteMemoryEntryAsync(entry);
    }

    private void OnMemoryHistory(object sender, RoutedEventArgs e)
    {
        var entry = _viewModel.SelectedMemoryEntry;
        if (entry is null)
        {
            _dialogs.Info("请先在记忆列表中选择一条记忆。", "历史版本");
            return;
        }
        _ = _viewModel.LoadMemoryHistoryAsync(entry);
    }

    private void OnRollbackMemory(object sender, RoutedEventArgs e)
    {
        var entry = _viewModel.SelectedMemoryEntry;
        var version = _viewModel.SelectedMemoryVersion;
        if (entry is null || version is null)
        {
            _dialogs.Info("请先选择一条记忆，并在右侧选择要回滚的历史版本。", "回滚记忆");
            return;
        }
        if (_dialogs.Confirm(
                $"确定把 [{entry.Kind}:{entry.Key}] 回滚到 v{version.Version}？\n\n当前状态会先归档，回滚本身也可以再次撤销。",
                "回滚记忆"))
            _ = _viewModel.RollbackMemoryAsync(entry, version);
    }

    private void OnArchiveMemory(object sender, RoutedEventArgs e)
    {
        var entry = _viewModel.SelectedMemoryEntry;
        if (entry is null)
        {
            _dialogs.Info("请先在记忆列表中选择一条记忆。", "归档记忆");
            return;
        }
        if (_dialogs.Confirm(
                $"确定归档 [{entry.Kind}:{entry.Key}]？\n\n归档后状态为 superseded，不再被召回；历史版本保留，可随时回滚恢复。",
                "归档记忆"))
            _ = _viewModel.ArchiveMemoryEntryAsync(entry);
    }

    private void OnClearMemory(object sender, RoutedEventArgs e)
    {
        // 清空是破坏性操作（换 Embeddings 模型场景）：两次独立确认，防止误触
        var r1 = System.Windows.MessageBox.Show(
            "将删除记忆库全部条目（含会话片段/任务复盘/脚本/资产经验），用于更换 Embeddings 模型或向量维度等场景。\n\n此操作不可恢复！确定继续？",
            "清空记忆库（1/2）", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (r1 != System.Windows.MessageBoxResult.Yes)
            return;
        var r2 = System.Windows.MessageBox.Show(
            "再次确认：清空后所有历史向量与结构化经验将永久删除，且无法找回。之后请用「测试 Embeddings」验证新模型，并让 AI 重新登记脚本/经验。\n\n最终确认清空？",
            "清空记忆库（2/2）", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (r2 == System.Windows.MessageBoxResult.Yes)
            _ = _viewModel.ClearAllMemoryAsync();
    }

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
