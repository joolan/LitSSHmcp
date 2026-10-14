using System.Windows;
using System.Windows.Input;
using LitSSHmcp.Core.Services.Security;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public enum AppLockPasswordDialogMode
{
    /// <summary>首次设置密码。</summary>
    Set,

    /// <summary>修改密码（先验旧密码）。</summary>
    Change,

    /// <summary>仅验证当前密码（关闭应用锁等敏感操作前）。</summary>
    Verify
}

/// <summary>
/// 应用锁密码对话框：设置 / 修改 / 验证。返回 true 表示操作成功
/// （Set=已设置、Change=已修改、Verify=密码正确）。失败递进延迟与解锁窗口一致。
/// </summary>
public partial class AppLockPasswordDialog : FluentWindow
{
    private readonly AppLockService _lockService;
    private readonly AppLockPasswordDialogMode _mode;
    private int _failCount;
    private bool _busy;
    private bool _success;

    private AppLockPasswordDialog(AppLockService lockService, AppLockPasswordDialogMode mode)
    {
        _lockService = lockService;
        _mode = mode;
        InitializeComponent();

        switch (mode)
        {
            case AppLockPasswordDialogMode.Set:
                HeaderText.Text = "设置应用锁密码";
                OkButton.Content = "设置";
                break;
            case AppLockPasswordDialogMode.Change:
                HeaderText.Text = "修改应用锁密码";
                OkButton.Content = "修改";
                break;
            default:
                HeaderText.Text = "验证当前密码";
                NewLabel.Visibility = Visibility.Collapsed;
                NewBox.Visibility = Visibility.Collapsed;
                ConfirmLabel.Visibility = Visibility.Collapsed;
                ConfirmBox.Visibility = Visibility.Collapsed;
                OkButton.Content = "验证";
                Height = 240;
                break;
        }

        Loaded += (_, _) => (_mode == AppLockPasswordDialogMode.Verify ? OldBox : NewBox).Focus();
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
    }

    /// <summary>模态显示。true=操作成功。</summary>
    public static bool Show(Window? owner, AppLockService lockService, AppLockPasswordDialogMode mode)
    {
        var dialog = new AppLockPasswordDialog(lockService, mode);
        if (owner is not null)
            dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog._success;
    }

    private async void OnOk(object sender, RoutedEventArgs e) => await SubmitAsync();

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _busy)
            return;
        e.Handled = true;
        _ = SubmitAsync();
    }

    private async Task SubmitAsync()
    {
        if (_busy)
            return;
        _busy = true;
        OkButton.IsEnabled = false;
        try
        {
            if (_mode == AppLockPasswordDialogMode.Verify)
            {
                var password = OldBox.Password;
                if (string.IsNullOrEmpty(password))
                {
                    ErrorText.Text = "请输入密码。";
                    return;
                }
                if (_failCount > 0)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(_failCount, 5)));
                if (await _lockService.VerifyAsync(password))
                {
                    _success = true;
                    Close();
                }
                else
                {
                    _failCount++;
                    ErrorText.Text = "密码错误。";
                    OldBox.Clear();
                    OldBox.Focus();
                }
                return;
            }

            // Set / Change
            var newPassword = NewBox.Password;
            if (_mode == AppLockPasswordDialogMode.Change)
            {
                var oldPassword = OldBox.Password;
                if (string.IsNullOrEmpty(oldPassword))
                {
                    ErrorText.Text = "请输入当前密码。";
                    return;
                }
                if (_failCount > 0)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(_failCount, 5)));
                if (!await _lockService.VerifyAsync(oldPassword))
                {
                    _failCount++;
                    ErrorText.Text = "当前密码错误。";
                    OldBox.Clear();
                    OldBox.Focus();
                    return;
                }
            }

            if (newPassword.Length < AppLockService.MinPasswordLength)
            {
                ErrorText.Text = $"密码至少 {AppLockService.MinPasswordLength} 位。";
                return;
            }
            if (newPassword != ConfirmBox.Password)
            {
                ErrorText.Text = "两次输入的密码不一致。";
                ConfirmBox.Clear();
                return;
            }

            await _lockService.SetPasswordAsync(newPassword);
            _success = true;
            Close();
        }
        catch (Exception ex)
        {
            ErrorText.Text = "操作失败：" + ex.Message;
        }
        finally
        {
            OkButton.IsEnabled = true;
            _busy = false;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
