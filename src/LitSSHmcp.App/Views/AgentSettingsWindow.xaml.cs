using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Storage;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class AgentSettingsWindow : FluentWindow
{
    private readonly AgentSettingsViewModel _viewModel = new();

    public AgentSettingsWindow(IConfigService configService)
    {
        InitializeComponent();
        DataContext = _viewModel;

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

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        var provider = _viewModel.SelectedProvider;
        if (provider is null)
            return;
        var result = System.Windows.MessageBox.Show(
            $"确定删除模型「{provider.Name}」？删除后立即生效。",
            "删除模型", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
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
}
