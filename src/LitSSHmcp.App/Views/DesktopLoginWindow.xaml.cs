using System.Windows;
using LitSSHmcp.App.Services;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>
/// 桌面登录对话框（评估报告 D1 收尾）：校验 Web 账号后切换归因（新会话 owner / 审计 UserId），
/// 桌面不因此获得或失去任何权限（仅归因不限权）。
/// </summary>
public partial class DesktopLoginWindow : FluentWindow
{
    private readonly DesktopLoginService _service;

    public DesktopLoginWindow(DesktopLoginService service)
    {
        _service = service;
        InitializeComponent();
        Loaded += (_, _) => UsernameBox.Focus();
    }

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        LoginButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var (ok, error) = await _service.LoginAsync(UsernameBox.Text, PasswordBox.Password);
            if (!ok)
            {
                ErrorText.Text = error ?? "登录失败";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
            DialogResult = true;
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
