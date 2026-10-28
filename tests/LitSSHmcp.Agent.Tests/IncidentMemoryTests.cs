using LitSSHmcp.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class IncidentMemoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"litssh-incident-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* 忽略 */ }
        }
    }

    private async Task<(AgentMemoryService Svc, AgentMemoryStore Store)> CreateAsync()
    {
        var store = new AgentMemoryStore(_dbPath);
        var svc = new AgentMemoryService(store, new FixedEmbedder(), 5, 0.15);
        await svc.InitializeAsync();
        return (svc, store);
    }

    [Fact]
    public async Task Save_list_and_filter_incident_by_asset()
    {
        var (svc, store) = await CreateAsync();

        var saved = await svc.SaveIncidentAsync("磁盘满恢复", "df 100%", "ssh:web01",
            "journald 日志膨胀", "journalctl --vacuum-size=200M", "df 恢复", "snapshot:101", 0.8, "resolved");
        Assert.Contains("已记录事件", saved);
        await svc.SaveIncidentAsync("nginx 502 恢复", "502 Bad Gateway", "ssh:web02",
            "upstream 超时", "调整 proxy_read_timeout", "恢复", "tool:log_query", 0.7, "resolved");

        var web01 = await svc.ListIncidentsAsync("ssh:web01", null, 20);
        Assert.Contains("磁盘满恢复", web01);
        Assert.DoesNotContain("nginx 502", web01);

        var allResolved = await svc.ListIncidentsAsync(null, "resolved", 20);
        Assert.Contains("磁盘满恢复", allResolved);
        Assert.Contains("nginx 502", allResolved);

        var incident = (await store.ListIncidentsAsync(null, null, 20)).Single(i => i.Title == "磁盘满恢复");
        var assets = await store.ListMemoryAssetIdsAsync("incident", incident.Id);
        Assert.Equal(new[] { "ssh:web01" }, assets);
    }

    [Fact]
    public async Task Supersede_incident_marks_old_and_links_new()
    {
        var (svc, store) = await CreateAsync();
        await svc.SaveIncidentAsync("旧路径", "现象", "ssh:s1", "旧根因", "旧处置", "恢复", "", 0.5, "resolved");
        await svc.SaveIncidentAsync("新路径", "现象", "ssh:s1", "新根因", "新处置", "恢复", "", 0.9, "resolved");
        var incidents = await store.ListIncidentsAsync(null, null, 20);
        var oldIncident = incidents.Single(i => i.Title == "旧路径");
        var newIncident = incidents.Single(i => i.Title == "新路径");

        var result = await svc.SupersedeIncidentAsync(oldIncident.Id, newIncident.Id);

        Assert.Contains("superseded", result);
        var updated = await store.GetIncidentAsync(oldIncident.Id);
        Assert.NotNull(updated);
        Assert.Equal("superseded", updated!.Status);
        Assert.Equal(newIncident.Id, updated.SupersededBy);
        Assert.Contains("未找到新事件", await svc.SupersedeIncidentAsync(oldIncident.Id, 99999));
    }

    [Fact]
    public async Task Structured_memory_syncs_memory_asset_relation()
    {
        var (svc, store) = await CreateAsync();
        await svc.SaveAsync("task", "disk", "磁盘满: 先 df 定位再清理日志", "ssh:s1,ds:d1", null);
        var record = await store.FindByKeyAsync("task", "disk");
        Assert.NotNull(record);
        Assert.Equal(new[] { "ds:d1", "ssh:s1" },
            (await store.ListMemoryAssetIdsAsync("memory", record!.Id)).OrderBy(a => a, StringComparer.Ordinal));

        await svc.UpdateAsync("task", "disk", content: null, assetIds: "ssh:s2", tags: null, status: null);
        Assert.Equal(new[] { "ssh:s2" }, await store.ListMemoryAssetIdsAsync("memory", record.Id));

        await svc.DeleteAsync("task", "disk");
        Assert.Empty(await store.ListMemoryAssetIdsAsync("memory", record.Id));
    }

    [Fact]
    public async Task Incident_rejects_sensitive_content()
    {
        var (svc, _) = await CreateAsync();

        var result = await svc.SaveIncidentAsync("登录失败", "password=hunter2", "ssh:s1",
            "", "", "", "", 0.5, "open");

        Assert.Contains("敏感", result);
        Assert.Equal("(暂无匹配的事件记录)", await svc.ListIncidentsAsync(null, null, 20));
    }

    [Fact]
    public async Task Private_incident_is_not_visible_to_other_member()
    {
        var aliceStore = new AgentMemoryStore(_dbPath, new MemoryAccessScope("1", IsAdmin: false));
        var alice = new AgentMemoryService(aliceStore, new FixedEmbedder(), 5, 0.15);
        await alice.InitializeAsync();
        await alice.SaveIncidentAsync("alice 事件", "现象", "ssh:s1", "", "", "", "", 0.5, "resolved");

        var bobStore = new AgentMemoryStore(_dbPath, new MemoryAccessScope("2", IsAdmin: false));
        var bob = new AgentMemoryService(bobStore, new FixedEmbedder(), 5, 0.15);
        await bob.InitializeAsync();

        Assert.Equal("(暂无匹配的事件记录)", await bob.ListIncidentsAsync(null, null, 20));
        Assert.Contains("alice 事件", await alice.ListIncidentsAsync(null, null, 20));
    }

    [Fact]
    public async Task Incident_tools_expose_read_and_write()
    {
        var (svc, _) = await CreateAsync();
        var tools = IncidentTools.Create(svc);

        Assert.Contains(tools, t => t.Name == "ops_incident_list" && t.ReadOnly);
        Assert.Contains(tools, t => t.Name == "ops_incident_write" && !t.ReadOnly);

        var write = tools.Single(t => t.Name == "ops_incident_write");
        var result = (await write.InvokeAsync(new Dictionary<string, object?>
        {
            ["mode"] = "save",
            ["title"] = "事件工具测试",
            ["symptom"] = "测试现象",
            ["assetIds"] = "ssh:s1"
        }, default))?.ToString() ?? string.Empty;

        Assert.Contains("已记录事件", result);
    }

    private sealed class FixedEmbedder : IEmbeddingGenerator<string, Embedding<float>>, IDisposable
    {
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = values.Select(_ => new Embedding<float>(new[] { 1f, 0f })).ToList();
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
        }
    }
}
