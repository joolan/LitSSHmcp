using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class ContextStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "litssh-agent-" + Guid.NewGuid().ToString("N") + ".db");
    private ContextStore NewStore() => new(_dbPath);

    [Fact]
    public async Task Session_and_messages_roundtrip()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var id = await store.CreateSessionAsync("会话1");
        await store.AppendMessageAsync(id, "user", "你好");
        await store.AppendMessageAsync(id, "assistant", "在的");

        var sessions = await store.ListSessionsAsync();
        Assert.Single(sessions);
        Assert.Equal("会话1", sessions[0].Title);

        var messages = await store.GetMessagesAsync(id);
        Assert.Equal(2, messages.Count);
        Assert.Equal("user", messages[0].Role);
        Assert.Equal("在的", messages[1].Content);
    }

    [Fact]
    public async Task Session_remembers_last_used_provider()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.CreateSessionAsync("会话");
        Assert.Null((await store.ListSessionsAsync())[0].ProviderId);

        await store.SetSessionProviderAsync(id, "model-xyz");
        Assert.Equal("model-xyz", (await store.ListSessionsAsync())[0].ProviderId);
    }

    [Fact]
    public async Task Rename_and_delete()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.CreateSessionAsync("旧");
        await store.AppendMessageAsync(id, "user", "x");

        await store.RenameSessionAsync(id, "新");
        Assert.Equal("新", (await store.ListSessionsAsync())[0].Title);

        await store.DeleteSessionAsync(id);
        Assert.Empty(await store.ListSessionsAsync());
        Assert.Empty(await store.GetMessagesAsync(id));
    }

    [Fact]
    public async Task ReplaceMessages_replaces_full_transcript()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.CreateSessionAsync("s");
        await store.AppendMessageAsync(id, "user", "旧");
        await store.AppendMessageAsync(id, "assistant", "旧答");

        await store.ReplaceMessagesAsync(id, new[]
        {
            ("user", "新"),
            ("assistant", "新答")
        });

        var messages = await store.GetMessagesAsync(id);
        Assert.Equal(2, messages.Count);
        Assert.Equal("新", messages[0].Content);
        Assert.Equal("新答", messages[1].Content);
    }

    [Fact]
    public async Task Prune_caps_messages_and_sessions()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var a = await store.CreateSessionAsync("a");
        for (var i = 0; i < 5; i++)
            await store.AppendMessageAsync(a, "user", "m" + i);
        await store.PruneAsync(maxSessions: 0, maxMessagesPerSession: 2, retentionDays: 0);
        Assert.Equal(2, (await store.GetMessagesAsync(a)).Count);

        await Task.Delay(10);
        var b = await store.CreateSessionAsync("b");
        await Task.Delay(10);
        var c = await store.CreateSessionAsync("c");
        await store.PruneAsync(maxSessions: 2, maxMessagesPerSession: 0, retentionDays: 0);

        var sessions = await store.ListSessionsAsync();
        Assert.Equal(2, sessions.Count);
        Assert.DoesNotContain(sessions, s => s.Id == a);   // 最旧的会话被裁掉
        Assert.Contains(sessions, s => s.Id == c);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* ignore */ }
    }
}
