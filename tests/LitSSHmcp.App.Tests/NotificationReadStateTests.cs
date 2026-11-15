using LitSSHmcp.App.Services.Notifications;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class NotificationReadStateTests
{
    [Fact]
    public void Notification_item_reports_unread_state()
    {
        var item = new NotificationItem
        {
            Source = "test",
            Title = "title",
            Message = "message"
        };

        Assert.True(item.IsUnread);

        item.IsRead = true;

        Assert.False(item.IsUnread);
    }
}
