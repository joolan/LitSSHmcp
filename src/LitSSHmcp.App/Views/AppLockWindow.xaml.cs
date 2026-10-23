using System.Windows;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public enum AppLockWindowMode
{
    /// <summary>首次使用：设置应用锁密码（可「暂不启用」）。</summary>
    SetPassword,

    /// <summary>已设置密码：输入密码（或 Windows Hello）解锁。</summary>
    Unlock
}

/// <summary>
/// 应用锁窗口：启动时密码门卫 / 系统锁屏回来后的重锁。
/// SetPassword 模式可「暂不启用」跳过；Unlock 模式解锁成功触发 <see cref="Unlocked"/>，
/// relock（系统锁屏回来）时不允许直接关闭绕过。
/// </summary>
public partial class AppLockWindow : FluentWindow
{
    private readonly AppLockService _lockService;
    private readonly AppLockWindowMode _mode;
    private readonly bool _relock;
    private int _failCount;
    private bool _busy;
    private bool _allowClose;

    /// <summary>解锁成功（密码或 Windows Hello）。</summary>
    public event EventHandler? Unlocked;

    /// <summary>SetPassword 模式下用户选择「暂不启用」（继续以未锁定状态进入主界面）。</summary>
    public event EventHandler? Skipped;

    public AppLockWindow(AppLockService lockService, AppLockWindowMode mode, bool relock)
    {
        _lockService = lockService;
        _mode = mode;
        _relock = relock;

        InitializeComponent();
        Loaded += OnLoaded;
        Closing += (_, e) =>
        {
            // 系统锁屏回来的重锁：不允许点 X 绕过（只能解锁或显式退出）
            if (_relock && !_allowClose)
                e.Cancel = true;
        };

        ConfigureForMode();
    }

    private void ConfigureForMode()
    {
        if (_mode == AppLockWindowMode.SetPassword)
        {
            HeaderText.Text = "设置应用锁密码";
            HintText.Text = "为本程序设置启动密码：每次打开需要输入密码（或用 Windows Hello），防止他人拿到电脑后直接操作。密码哈希加密存储，明文不落盘。";
            PasswordLabel.Text = "密码（至少 6 位）";
            ConfirmBox.Visibility = Visibility.Visible;
            ConfirmLabel.Visibility = Visibility.Visible;
            PrimaryButton.Content = "启用应用锁";
            SecondaryButton.Content = "暂不启用";
            SecondaryButton.Visibility = Visibility.Visible;
            Height = 380;
        }
        else
        {
            HeaderText.Text = "输入密码解锁";
            HintText.Text = "LitSSH 已锁定，输入应用锁密码继续（或使用 Windows Hello）。";
            PasswordLabel.Text = "密码";
            ConfirmBox.Visibility = Visibility.Collapsed;
            ConfirmLabel.Visibility = Visibility.Collapsed;
            PrimaryButton.Content = "解锁";
            SecondaryButton.Content = "退出程序";
            SecondaryButton.Visibility = Visibility.Visible;
            Height = 330;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => AsyncOperation.Run("初始化应用锁", async () =>
    {
        PasswordBox.Focus();

        if (_mode == AppLockWindowMode.Unlock && await WindowsHelloService.IsAvailableAsync())
        {
            HelloButton.Visibility = Visibility.Visible;
            Height += 44;
        }
    });

    private void OnPrimary(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("提交应用锁", SubmitAsync);

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

        if (_mode == AppLockWindowMode.SetPassword)
        {
            var password = PasswordBox.Password;
            var confirm = ConfirmBox.Password;
            if (password.Length < AppLockService.MinPasswordLength)
            {
                ErrorText.Text = $"密码至少 {AppLockService.MinPasswordLength} 位。";
                return;
            }
            if (password != confirm)
            {
                ErrorText.Text = "两次输入的密码不一致。";
                ConfirmBox.Clear();
                return;
            }

            _busy = true;
            ErrorText.Text = string.Empty;
            try
            {
                await _lockService.SetPasswordAsync(password);
                Audit("app_lock_set", success: true, "password");
                _allowClose = true;
                Unlocked?.Invoke(this, EventArgs.Empty);
                Close();
            }
            catch (Exception ex)
            {
                ErrorText.Text = "设置失败：" + ex.Message;
            }
            finally
            {
                _busy = false;
            }
            return;
        }

        // Unlock 模式：失败递进延迟（1s 起、封顶 5s），略微抬高暴力试错成本
        _busy = true;
        try
        {
            var password = PasswordBox.Password;
            if (string.IsNullOrEmpty(password))
            {
                ErrorText.Text = "请输入密码。";
                return;
            }

            if (_failCount > 0)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(_failCount, 5)));

            if (await _lockService.VerifyAsync(password))
            {
                Audit("app_lock_unlock", success: true, "password");
                _allowClose = true;
                Unlocked?.Invoke(this, EventArgs.Empty);
                Close();
            }
            else
            {
                _failCount++;
                ErrorText.Text = "密码错误。";
                PasswordBox.Clear();
                PasswordBox.Focus();
                Audit("app_lock_unlock", success: false, "password");
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnHello(object sender, RoutedEventArgs e) => AsyncOperation.Run("Windows Hello 解锁", async () =>
    {
        if (_busy)
            return;
        _busy = true;
        HelloButton.IsEnabled = false;
        try
        {
            if (await WindowsHelloService.VerifyAsync("解锁 LitSSH 应用锁"))
            {
                Audit("app_lock_unlock", success: true, "hello");
                _allowClose = true;
                Unlocked?.Invoke(this, EventArgs.Empty);
                Close();
            }
            else
            {
                ErrorText.Text = "Windows Hello 验证未通过。";
            }
        }
        finally
        {
            HelloButton.IsEnabled = true;
            _busy = false;
        }
    });

    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        if (_mode == AppLockWindowMode.SetPassword)
        {
            _allowClose = true;
            Skipped?.Invoke(this, EventArgs.Empty);
            Close();
            return;
        }

        // Unlock：显式退出（重锁时 X 已被拦截，这里保留明确的退出途径）
        _allowClose = true;
        Application.Current.Shutdown();
    }

    /// <summary>解锁/设置结果写入审计（best-effort，失败不影响解锁流程）。</summary>
    private static void Audit(string command, bool success, string method)
    {
        try
        {
            var audit = new AuditLogService();
            audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = "app-lock",
                ServerName = "应用锁",
                Command = command,
                Status = success ? CommandStatus.Executed : CommandStatus.Blocked,
                Category = AuditCategory.Gate,
                Decision = success ? "auto-approve" : "auto-reject",
                Result = method,
                Tool = "app_lock"
            }).GetAwaiter().GetResult();
        }
        catch
        {
            // 审计失败不影响解锁
        }
    }
}
