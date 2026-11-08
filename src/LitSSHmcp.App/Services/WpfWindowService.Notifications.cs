using LitSSHmcp.App.Views;

namespace LitSSHmcp.App.Services;

public sealed partial class WpfWindowService
{
    public NotificationCenterWindow CreateNotificationCenterWindow() => new() { Topmost = false };
}
