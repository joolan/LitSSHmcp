using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Views;

public partial class SecuritySettingsView : UserControl
{
    private SecuritySettingsViewModel _viewModel = new();
    private AppLockService _appLockService = new(new ConfigService());

    public SecuritySettingsView()
    {
        InitializeComponent();
        DataContext = _viewModel;
        HostKeyModeBox.ItemsSource = new[] { "Tofu", "Strict", "Off" };
        ApprovalStyleBox.ItemsSource = new[] { "process", "dialog", "native" };
        Loaded += async (_, _) => await RefreshAppLockStatusAsync();
    }

    public void Initialize(IAppServices? services)
    {
        _viewModel = new SecuritySettingsViewModel(services?.Config);
        _appLockService = services is null
            ? new AppLockService(new ConfigService())
            : new AppLockService(services.Config);
        DataContext = _viewModel;
    }

    private void OnReload(object sender, RoutedEventArgs e) => _viewModel.Load();

    private void OnSave(object sender, RoutedEventArgs e) => _viewModel.Save();

    private void OnAuthzAddRule(object sender, RoutedEventArgs e) =>
        _viewModel.AuthzRules.Add(new AuthzRuleRow());

    private void OnAuthzRemoveRule(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AuthzRuleRow row })
            _viewModel.AuthzRules.Remove(row);
    }

    private void OnResetWebJobsRoot(object sender, RoutedEventArgs e) => _viewModel.WebJobsRoot = string.Empty;

    private async Task RefreshAppLockStatusAsync()
    {
        try
        {
            var enabled = await _appLockService.IsEnabledAsync();
            AppLockStatusText.Text = enabled
                ? "状态：已启用 —— 每次启动需解锁（密码或 Windows Hello）"
                : "状态：未启用 —— 任何能打开这台电脑的人都可直接进入";
            AppLockSetButton.Content = enabled ? "修改密码…" : "设置密码…";
            AppLockDisableButton.IsEnabled = enabled;

            var hello = await WindowsHelloService.IsAvailableAsync();
            AppLockHelloText.Text = hello ? "（本机可用 Windows Hello 快捷解锁）" : string.Empty;
        }
        catch
        {
            AppLockStatusText.Text = "状态：读取失败";
        }
    }

    private void OnAppLockSetPassword(object sender, RoutedEventArgs e) => AsyncOperation.Run("设置应用锁", async () =>
    {
        try
        {
            var configured = await _appLockService.IsEnabledAsync();
            var mode = configured ? AppLockPasswordDialogMode.Change : AppLockPasswordDialogMode.Set;
            if (AppLockPasswordDialog.Show(Window.GetWindow(this), _appLockService, mode))
                await RefreshAppLockStatusAsync();
        }
        catch (Exception ex)
        {
            AppLockStatusText.Text = "设置失败：" + ex.Message;
        }
    });

    private void OnAppLockDisable(object sender, RoutedEventArgs e) => AsyncOperation.Run("关闭应用锁", async () =>
    {
        try
        {
            if (!await _appLockService.IsEnabledAsync())
            {
                await RefreshAppLockStatusAsync();
                return;
            }
            if (!AppLockPasswordDialog.Show(Window.GetWindow(this), _appLockService, AppLockPasswordDialogMode.Verify))
                return;
            await _appLockService.DisableAsync();
            await RefreshAppLockStatusAsync();
        }
        catch (Exception ex)
        {
            AppLockStatusText.Text = "关闭失败：" + ex.Message;
        }
    });
}
