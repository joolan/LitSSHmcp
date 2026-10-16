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

    [Fact]
    public async Task List_includeRaw_and_deleteById()
    {
        var store = NewStore();
        await store.InitializeAsync();

        // raw 会话片段 + 结构化记忆混存
        await store.AddAsync("会话", "raw 片段内容", new[] { 1f, 0f });
        var id = await store.AddStructuredAsync("task", "reboot-web", "会话", "重启 web 后要检查证书",
            new[] { 0f, 1f }, "", "nginx", 1);
        Assert.True(id > 0);

        // 默认过滤 raw；includeRaw=true 时包含
        var structuredOnly = await store.ListAsync(null, null, 50);
        Assert.Single(structuredOnly);
        Assert.Equal("task", structuredOnly[0].Kind);

        var all = await store.ListAsync(null, null, 50, includeRaw: true);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, r => r.Kind == "raw");

        // kind 过滤
        Assert.Single(await store.ListAsync("task", null, 50, includeRaw: true));

        // 按 Id 删除（含 raw 行）
        Assert.True(await store.DeleteByIdAsync(id));
        Assert.False(await store.DeleteByIdAsync(id));   // 已删再删返回 false
        Assert.Empty(await store.ListAsync(null, null, 50));
        Assert.Single(await store.ListAsync(null, null, 50, includeRaw: true));
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* ignore */ }
    }
}
