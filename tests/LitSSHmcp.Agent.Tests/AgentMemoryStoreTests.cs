using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class AgentMemoryStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "litssh-mem-" + Guid.NewGuid().ToString("N") + ".db");
    private AgentMemoryStore NewStore() => new(_dbPath);

    [Fact]
    public async Task Add_search_exists_count_clear()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await store.AddAsync("会话", "nginx 配置", new[] { 1f, 0f });
        await store.AddAsync("文档", "mysql 慢查询", new[] { 0f, 1f });
        await store.AddAsync("会话", "nginx 证书", new[] { 0.9f, 0.1f });

        Assert.Equal(3, await store.CountAsync());
        Assert.True(await store.ExistsAsync("会话", "nginx 配置"));

        var hits = await store.SearchAsync(new[] { 1f, 0f }, 2);
        Assert.Equal(2, hits.Count);
        Assert.Equal("nginx 配置", hits[0].Content);          // 最相近
        Assert.True(hits[0].Score >= hits[1].Score);

        await store.ClearAsync();
        Assert.Equal(0, await store.CountAsync());
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* ignore */ }
    }
}
