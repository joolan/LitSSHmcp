using System.Windows;
using LitSSHmcp.App.Services;
using Wpf.Ui.Controls;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;

namespace LitSSHmcp.App.Views.Tools;

public partial class VaultChangePasswordWindow : FluentWindow
{
    private readonly Func<string, string, Task<string?>>? _changePassword;

    public VaultChangePasswordWindow(string vaultName,
        Func<string, string, Task<string?>>? changePassword = null)
    {
        InitializeComponent();
        _changePassword = changePassword;
        VaultNameText.Text = vaultName;
        Loaded += (_, _) => OldPasswordBox.Focus();
    }

    public string OldPassword => OldPasswordBox.Password;
    public string NewPassword => NewPasswordBox.Password;

    private void OnChange(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("修改主密码", ChangeAsync);

    private async Task ChangeAsync()
    {
        if (string.IsNullOrWhiteSpace(OldPassword))
        {
            ErrorText.Text = "请输入旧主密码。";
            return;
        }
        if (NewPassword.Length < 8)
        {
            ErrorText.Text = "新主密码至少 8 位。";
            return;
        }
        if (!string.Equals(NewPassword, ConfirmPasswordBox.Password, StringComparison.Ordinal))
        {
            ErrorText.Text = "两次输入的新主密码不一致。";
            return;
        }

        if (_changePassword is not null)
        {
            var error = await _changePassword(OldPassword, NewPassword);
            if (error is not null)
            {
                ErrorText.Text = error;
                return;
            }
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
