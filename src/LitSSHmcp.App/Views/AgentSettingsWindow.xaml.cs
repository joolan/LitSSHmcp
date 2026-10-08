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

        if (string.Equals(initialTab, "model", StringComparison.OrdinalIgnoreCase) && Tabs.Items.Count > 1)
            Tabs.SelectedIndex = 1;
        else if (string.Equals(initialTab, "archive", StringComparison.OrdinalIgnoreCase) && Tabs.Items.Count > 2)
            Tabs.SelectedIndex = 2;

        // 切到「归档记录」时刷新列表
        Tabs.SelectionChanged += (_, _) =>
        {
            if (Tabs.SelectedIndex == 2)
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

    private void OnAddModel(object sender, RoutedEventArgs e) => _viewModel.AddModel();

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
