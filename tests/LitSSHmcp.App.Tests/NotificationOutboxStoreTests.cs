using System.IO;
using LitSSHmcp.App.Services.Notifications;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class NotificationOutboxStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(),
        "litssh-notify-" + Guid.NewGuid().ToString("N") + ".db");

    [Fact]
    public async Task Enqueue_then_mark_sent_removes_from_pending()
    {
        var store = new NotificationOutboxStore(_dbPath);
        var channel = new NotificationChannelConfig { Id = "ch", Name = "test", Type = "webhook", Url = "http://127.0.0.1" };
        var id = await store.EnqueueAsync(new NotificationMessage(
            NotificationTypes.AiTaskFailed, "AI 任务", "失败", "body", NotificationSeverity.Error), channel);

        Assert.True(id > 0);
        Assert.Single(await store.GetPendingAsync());
        await store.MarkSentAsync(id);
        Assert.Empty(await store.GetPendingAsync());
    }

    [Fact]
    public async Task Failed_message_is_rescheduled_or_dead()
    {
        var store = new NotificationOutboxStore(_dbPath);
        var channel = new NotificationChannelConfig { Id = "ch", Name = "test", Type = "webhook" };
        var id = await store.EnqueueAsync(new NotificationMessage(
            NotificationTypes.HealthCritical, "健康", "风险", "body", NotificationSeverity.Error), channel);

        var delivery = Assert.Single(await store.GetPendingAsync());
        await store.MarkFailedAsync(id, 5, "boom", DateTime.UtcNow.AddMinutes(1), dead: true);
        Assert.Empty(await store.GetPendingAsync());
        Assert.NotNull(delivery);
    }

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
