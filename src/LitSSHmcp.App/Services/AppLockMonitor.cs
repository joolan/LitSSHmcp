using System.Windows;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Win32;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 跟随 Windows 会话锁定：Win+L（或屏保）离开后，系统解锁回来时弹出应用锁窗口（模态盖住主窗口），
/// 必须重新通过密码 / Windows Hello 才能继续操作。
/// </summary>
public static class AppLockMonitor
{
    private static Window? _mainWindow;
    private static bool _sessionLocked;
    private static AppLockWindow? _relockWindow;

    public static void Attach(Application app, Window mainWindow)
    {
        _mainWindow = mainWindow;
        try
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            app.Exit += (_, _) =>
            {
                try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
            };
        }
        catch
        {
            // 拿不到系统事件（极端环境）：仅失去锁屏跟随，不影响应用锁本身
        }
    }

    private static void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            _sessionLocked = true;
            return;
        }

        if (e.Reason != SessionSwitchReason.SessionUnlock || !_sessionLocked)
            return;
        _sessionLocked = false;

        var window = _mainWindow;
        if (window is null || !window.IsLoaded)
            return;

        // SessionSwitch 在线程池线程触发，回 UI 线程
        window.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_relockWindow is { IsLoaded: true })
            {
                _relockWindow.Activate();
                return;
            }
            _ = ShowRelockAsync(window);
        }));
    }

    private static async Task ShowRelockAsync(Window owner)
    {
        var configService = new ConfigService();
        bool locked;
        try
        {
            var config = await configService.LoadConfigAsync().ConfigureAwait(false);
            locked = AppLockService.IsConfigured(config.AppLock);
        }
        catch
        {
            return;   // 配置读不了：跳过重锁（本次会话启动时已通过门卫，避免把用户锁死在异常状态）
        }
        if (!locked)
            return;

        await owner.Dispatcher.InvokeAsync(() =>
        {
            var window = new AppLockWindow(new AppLockService(configService), AppLockWindowMode.Unlock, relock: true)
            {
                Owner = owner
            };
            _relockWindow = window;
            window.Closed += (_, _) => _relockWindow = null;
            window.ShowDialog();   // 模态：解锁前主窗口不可交互
        });
    }
}
