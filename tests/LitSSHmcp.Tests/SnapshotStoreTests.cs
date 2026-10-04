using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Snapshot;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.Tests;

public class SnapshotStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "litssh-snap-" + Guid.NewGuid().ToString("N") + ".db");

    private SnapshotStore NewStore() => new(_dbPath);

    [Fact]
    public async Task Begin_complete_and_get_latest_roundtrips()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var id = (await store.BeginAsync("s1", "web01"))!.Value;
        var running = await store.GetRunningAsync("s1");
        Assert.NotNull(running);
        Assert.Equal(SnapshotStatus.Running, running!.Status);

        await store.CompleteAsync(id, "{\"sections\":{}}", 1234, "sudo", 1);

        var latest = await store.GetLatestAsync("s1");
        Assert.NotNull(latest);
        Assert.Equal(SnapshotStatus.Succeeded, latest!.Status);
        Assert.Equal("sudo", latest.Escalation);
        Assert.Equal(1234, latest.DurationMs);
        Assert.Null(await store.GetRunningAsync("s1"));
    }

    [Fact]
    public async Task Methods_lazily_create_schema_without_explicit_initialize()
    {
        // 回归: 桌面 App 未调用 InitializeAsync 就直接查询, 此前报 "no such table: Snapshots"。
        var store = NewStore();

        Assert.Empty(await store.GetRecentAsync("s1"));
        Assert.NotNull(await store.BeginAsync("s1", "web01"));
        Assert.NotNull(await store.GetLatestAsync("s1"));
    }

    [Fact]
    public async Task Begin_rejects_second_running_for_same_server()
    {
        var store = NewStore();
        await store.InitializeAsync();

        Assert.NotNull(await store.BeginAsync("s1", "web01"));
        Assert.Null(await store.BeginAsync("s1", "web01"));       // 跨进程单飞: 已有 Running
        Assert.NotNull(await store.BeginAsync("s2", "web02"));    // 其他服务器不受影响
    }

    [Fact]
    public async Task Fail_records_error_and_keeps_partial_data()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var id = (await store.BeginAsync("s1", "web01"))!.Value;
        await store.FailAsync(id, "connection refused", 500, "{\"sections\":{\"resource\":{\"status\":\"failed\"}}}");

        var latest = await store.GetLatestAsync("s1");
        Assert.Equal(SnapshotStatus.Failed, latest!.Status);
        Assert.Equal("connection refused", latest.Error);
        Assert.Contains("resource", latest.DataJson);
    }

    [Fact]
    public async Task Events_are_appended_in_order()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var id = (await store.BeginAsync("s1", "web01"))!.Value;
        await store.AppendEventAsync(id, "s1", "collector_started", "resource");
        await store.AppendEventAsync(id, "s1", "collector_completed", "resource", "ok");
        await store.CompleteAsync(id, "{}", 10, "direct", 1);

        var events = await store.GetEventsAsync(id);
        Assert.Equal(new[] { "started", "collector_started", "collector_completed" }, events.Select(e => e.Kind).ToArray());
        Assert.Equal("resource", events[1].Collector);
    }

    [Fact]
    public async Task Prune_keeps_newest_and_cascades_events()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var ids = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var id = (await store.BeginAsync("s1", "web01"))!.Value;
            await store.CompleteAsync(id, "{}", 10, "direct", 1);
            ids.Add(id);
        }

        var deleted = await store.PruneAsync("s1", keepPerServer: 2);

        Assert.Equal(1, deleted);
        var recent = await store.GetRecentAsync("s1", 10);
        Assert.Equal(2, recent.Count);
        Assert.DoesNotContain(recent, r => r.Id == ids[0]);
        Assert.NotNull(await store.GetByIdAsync(ids[1]));
        Assert.Empty(await store.GetEventsAsync(ids[0]));
    }

    [Fact]
    public async Task Prune_zero_means_unlimited()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = (await store.BeginAsync("s1", "web01"))!.Value;
        await store.CompleteAsync(id, "{}", 10, "direct", 1);

        Assert.Equal(0, await store.PruneAsync("s1", keepPerServer: 0));
        Assert.Single(await store.GetRecentAsync("s1", 10));
    }

    [Fact]
    public async Task Initialize_marks_orphan_running_as_failed()
    {
        var first = NewStore();
        await first.InitializeAsync();
        var id = (await first.BeginAsync("s1", "web01"))!.Value;   // 不完成, 模拟进程被杀

        var reopened = NewStore();
        await reopened.InitializeAsync();

        var orphan = await reopened.GetByIdAsync(id);
        Assert.Equal(SnapshotStatus.Failed, orphan!.Status);
        Assert.Contains("interrupted", orphan.Error);
        var events = await reopened.GetEventsAsync(id);
        Assert.Contains(events, e => e.Kind == "failed" && e.Message == "interrupted_by_restart");
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* ignore */ }
    }
}
