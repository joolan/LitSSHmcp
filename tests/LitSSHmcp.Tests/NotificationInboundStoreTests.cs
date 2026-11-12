using System.IO;
using LitSSHmcp.Core.Services.Notifications;
using Xunit;

namespace LitSSHmcp.Tests;

public class NotificationInboundStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(),
        "litssh-inbound-" + Guid.NewGuid().ToString("N") + ".db");

    [Fact]
    public async Task Enqueue_and_consume_roundtrip()
    {
        var store = new NotificationInboundStore(_dbPath);
        var id = await store.EnqueueAsync("web.login_failed", "Web 助手", "登录失败",
            "user=alice", "warning", "key-1");

        var pending = await store.GetPendingAsync();
        var item = Assert.Single(pending);
        Assert.Equal(id, item.Id);
        Assert.Equal("web.login_failed", item.Type);
        Assert.Equal("warning", item.Severity);

        await store.MarkConsumedAsync(id);
        Assert.Empty(await store.GetPendingAsync());
    }

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
