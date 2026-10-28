using System.Text.Json;
using LitSSHmcp.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class AssetProfileToolsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"litssh-profile-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public async Task Profile_combines_graph_neighborhood_impact_and_memory()
    {
        var store = new AgentMemoryStore(_dbPath);
        await store.InitializeAsync();
        await store.AddStructuredAsync("asset", "资产事实:ssh:s1:日志路径", "会话",
            "ssh:s1 的日志路径是 /var/log/app.log", new[] { 1f, 0f }, "ssh:s1", "", 1);
        await store.AddStructuredAsync("script", "脚本:清理日志", "会话",
            "journalctl --vacuum-size=200M", new[] { 1f, 0f }, "ssh:s1", "", 1);
        await store.AddIncidentAsync("磁盘满恢复", "df 100%", "ssh:s1", "journald",
            "清理日志", "恢复", "raw:1", 0.8, "resolved", "");
        var factRecord = await store.FindByKeyAsync("asset", "资产事实:ssh:s1:日志路径");
        Assert.NotNull(factRecord);
        Assert.Contains("ssh:s1", await store.ListMemoryAssetIdsAsync("memory", factRecord!.Id));
        Assert.NotEmpty(await store.ListByAssetAsync("ssh:s1", "asset", 10));

        var tool = AssetProfileTools.Create(store, new FakeTopologyTool());

        var text = (await tool.InvokeAsync(new Dictionary<string, object?>
        {
            ["assetId"] = "ssh:s1"
        }, default))?.ToString() ?? string.Empty;

        Assert.Contains("## 资产画像 ssh:s1", text);
        Assert.Contains("### 图谱（1 跳邻域）", text);
        Assert.Contains("app:order", text);
        Assert.Contains("### 影响面（谁依赖它）", text);
        Assert.Contains("domain:shop.example.com", text);
        Assert.Contains("### 历史事件", text);
        Assert.Contains("磁盘满恢复", text);
        Assert.Contains("### 资产事实 · ssh:s1", text);
        Assert.Contains("/var/log/app.log", text);
        Assert.Contains("### 脚本经验 · ssh:s1", text);
        Assert.Contains("journalctl", text);
    }

    [Fact]
    public async Task Profile_without_graph_or_memory_returns_hint()
    {
        var tool = AssetProfileTools.Create(null, null);

        var text = (await tool.InvokeAsync(new Dictionary<string, object?>
        {
            ["assetId"] = "ssh:none"
        }, default))?.ToString() ?? string.Empty;

        Assert.Contains("图谱与记忆均无可用信息", text);
    }

    private sealed class FakeTopologyTool : IAgentTool
    {
        public string Name => "topology_query";
        public AITool Tool => null!;
        public bool Destructive => false;
        public bool ReadOnly => true;
        public string? ToolGroup => "topology";

        public Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
        {
            var action = arguments?["action"]?.ToString();
            var json = action == "impact"
                ? """
                  {"result":{"root":{"id":"ssh:s1","type":"ssh","label":"web1"},
                   "impacted":[{"node":{"id":"domain:shop.example.com","type":"domain","label":"shop.example.com"},"hops":2,"via":"app -runsOn-> ssh"}]}}
                  """
                : """
                  {"result":{"root":{"id":"ssh:s1","type":"ssh","label":"web1"},
                   "nodes":[{"id":"app:order","type":"application","label":"order-service"}],
                   "edges":[{"from":"app:order","to":"ssh:s1","type":"runsOn"}]}}
                  """;
            return Task.FromResult<object?>(JsonDocument.Parse(json).RootElement.Clone());
        }
    }
}
