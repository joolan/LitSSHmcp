using LitSSHmcp.Agent;
using Microsoft.Data.Sqlite;
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
    public async Task Search_hybrid_fts_boosts_lexical_matches()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.AddStructuredAsync("task", "disk", "会话", "磁盘满时清理 journald",
            new[] { 1f, 0f }, "", "", 1);
        await store.AddStructuredAsync("task", "nginx", "会话", "nginx 配置证书检查",
            new[] { 1f, 0f }, "", "", 1);

        var hits = await store.SearchAsync(new[] { 1f, 0f }, 2, queryText: "磁盘");

        Assert.Equal(2, hits.Count);
        Assert.Equal("磁盘满时清理 journald", hits[0].Content);
    }

    [Fact]
    public async Task Search_can_exclude_raw_and_exposes_timestamps()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.AddAsync("会话", "raw 片段", new[] { 1f, 0f });
        await store.AddStructuredAsync("task", "k", "会话", "结构化经验", new[] { 1f, 0f }, "", "", 1);

        var structured = await store.SearchAsync(new[] { 1f, 0f }, 10, includeRaw: false);

        var hit = Assert.Single(structured);
        Assert.Equal("task", hit.Kind);
        Assert.False(string.IsNullOrWhiteSpace(hit.CreatedAt));
        Assert.False(string.IsNullOrWhiteSpace(hit.UpdatedAt));
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

    [Fact]
    public async Task Initialize_migrates_legacy_rows_as_team_memory()
    {
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE agent_memory (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Source TEXT NOT NULL,
    Content TEXT NOT NULL,
    Vector BLOB NOT NULL,
    CreatedAt TEXT NOT NULL
);
INSERT INTO agent_memory(Source, Content, Vector, CreatedAt)
VALUES ('会话', '旧版记忆', @vector, '2026-01-01T00:00:00Z');";
            cmd.Parameters.AddWithValue("@vector", new byte[8]);
            await cmd.ExecuteNonQueryAsync();
        }

        var store = NewStore();
        await store.InitializeAsync();
        var records = await store.ListAsync(null, null, 50, includeRaw: true);
        Assert.Single(records);
        Assert.Equal("旧版记忆", records[0].Content);
    }

    [Fact]
    public async Task Update_keeps_history_and_restore_brings_back_content()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.AddStructuredAsync("task", "disk", "会话", "v1 内容",
            new[] { 1f, 0f }, "ssh:s1", "", 1);

        await store.UpdateStructuredAsync(id, "v2 内容", new[] { 0f, 1f }, "ssh:s2", "tag", "active", 0);

        var versions = await store.ListVersionsAsync(id);
        Assert.Single(versions);
        Assert.Equal(1, versions[0].Version);
        Assert.Equal("v1 内容", versions[0].Content);
        Assert.Equal("ssh:s1", versions[0].AssetIds);

        Assert.True(await store.RestoreVersionAsync(id, 1));
        var current = await store.FindByKeyAsync("task", "disk");
        Assert.NotNull(current);
        Assert.Equal("v1 内容", current!.Content);
        Assert.Equal("ssh:s1", current.AssetIds);
        Assert.Equal(1, current.Freq);

        // 回滚前状态也被归档，回滚本身可撤销
        var after = await store.ListVersionsAsync(id);
        Assert.Equal(2, after.Count);
        Assert.Contains(after, v => v.Content == "v2 内容");
    }

    [Fact]
    public async Task Deleted_memory_can_be_restored_from_history()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.AddStructuredAsync("task", "deleted", "会话", "待恢复内容",
            new[] { 1f, 0f }, "", "", 1);

        Assert.True(await store.DeleteByIdAsync(id));
        Assert.Null(await store.FindByKeyAsync("task", "deleted"));
        Assert.Single(await store.ListVersionsAsync(id));

        Assert.True(await store.RestoreVersionAsync(id, 1));
        var restored = await store.FindByKeyAsync("task", "deleted");
        Assert.NotNull(restored);
        Assert.Equal("待恢复内容", restored!.Content);
    }

    [Fact]
    public async Task Archive_marks_superseded_and_keeps_history()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.AddStructuredAsync("task", "archive", "会话", "归档内容",
            new[] { 1f, 0f }, "", "", 1);

        Assert.True(await store.ArchiveByIdAsync(id));
        var archived = await store.FindByKeyAsync("task", "archive");
        Assert.NotNull(archived);
        Assert.Equal("superseded", archived!.Status);
        Assert.Single(await store.ListVersionsAsync(id));

        Assert.True(await store.RestoreVersionAsync(id, 1));
        Assert.Equal("active", (await store.FindByKeyAsync("task", "archive"))!.Status);
    }

    [Fact]
    public async Task Member_cannot_restore_team_memory_but_admin_can()
    {
        var adminStore = new AgentMemoryStore(_dbPath, new MemoryAccessScope("9", IsAdmin: true));
        await adminStore.InitializeAsync();
        var id = await adminStore.AddStructuredAsync("task", "team", "会话", "团队 v1",
            new[] { 1f, 0f }, "", "", 1);
        await adminStore.UpdateStructuredAsync(id, "团队 v2", new[] { 0f, 1f }, "", "", "active", 0);

        var member = new AgentMemoryStore(_dbPath, new MemoryAccessScope("1", IsAdmin: false));
        Assert.Single(await member.ListVersionsAsync(id));       // 团队历史可见
        Assert.False(await member.RestoreVersionAsync(id, 1));   // 但不可回滚
        Assert.Equal("团队 v2", (await adminStore.FindByKeyAsync("task", "team"))!.Content);

        Assert.True(await adminStore.RestoreVersionAsync(id, 1));
        Assert.Equal("团队 v1", (await adminStore.FindByKeyAsync("task", "team"))!.Content);
    }

    [Fact]
    public async Task History_is_capped_per_memory_and_can_be_pruned()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.AddStructuredAsync("task", "capped", "会话", "v0",
            new[] { 1f, 0f }, "", "", 1);

        for (var i = 1; i <= 45; i++)
            await store.UpdateStructuredAsync(id, $"v{i}", new[] { 1f, 0f }, "", "", "active", 0);

        var versions = await store.ListVersionsAsync(id);
        Assert.Equal(AgentMemoryStore.DefaultHistoryKeep, versions.Count);
        Assert.DoesNotContain(versions, v => v.Content == "v0");
        Assert.DoesNotContain(versions, v => v.Content == "v4");
        Assert.Contains(versions, v => v.Content == "v5");
        Assert.Contains(versions, v => v.Content == "v44");

        Assert.True(await store.PruneHistoryAsync(3, id) > 0);
        Assert.Equal(3, (await store.ListVersionsAsync(id)).Count);
    }

    [Fact]
    public async Task Clear_also_purges_history()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.AddStructuredAsync("task", "clear", "会话", "v1",
            new[] { 1f, 0f }, "", "", 1);
        await store.UpdateStructuredAsync(id, "v2", new[] { 0f, 1f }, "", "", "active", 0);
        Assert.Single(await store.ListVersionsAsync(id));

        await store.ClearAsync();

        Assert.Empty(await store.ListVersionsAsync(id));
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* ignore */ }
    }
}
