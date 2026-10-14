namespace LitSSHmcp.App.Services;

/// <summary>
/// 应用级退出标记：用户选择「退出程序」（关闭确认弹窗 / 托盘菜单）时置位，
/// 各独立窗口的 OnClosing 据此跳过「还有活动会话，确认关闭？」等二次确认，保证退出不被中断。
/// </summary>
public static class AppShutdown
{
    public static bool IsShuttingDown { get; set; }
}
