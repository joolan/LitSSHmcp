using LitSSHmcp.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class DiagnosisToolsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"litssh-diagnosis-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public async Task Report_only_mode_generates_structured_sections()
    {
        var tool = DiagnosisTools.Create(memory: null);
        Assert.True(tool.ReadOnly);

        var report = (await tool.InvokeAsync(new Dictionary<string, object?>
        {
            ["mode"] = "diagnose",
            ["title"] = "磁盘满恢复",
            ["symptom"] = "df 100%",
            ["evidence"] = "snapshot:101 / log_query",
            ["rootCause"] = "journald 膨胀",
            ["actions"] = "journalctl --vacuum-size=200M",
            ["verification"] = "df 恢复正常",
            ["impact"] = "ssh:web01",
            ["confidence"] = 0.9
        }, default))?.ToString() ?? string.Empty;

        Assert.Contains("## 诊断报告", report);
        Assert.Contains("现象", report);
        Assert.Contains("证据", report);
        Assert.Contains("根因", report);
        Assert.Contains("处置", report);
        Assert.Contains("验证", report);
        Assert.Contains("影响面", report);
        Assert.Contains("置信度", report);
    }

    [Fact]
    public async Task Change_mode_requires_verification_and_rollback()
    {
        var tool = DiagnosisTools.Create(memory: null);

        var missing = (await tool.InvokeAsync(new Dictionary<string, object?>
        {
            ["mode"] = "change",
            ["title"] = "重启 nginx",
            ["actions"] = "systemctl restart nginx"
        }, default))?.ToString() ?? string.Empty;
        Assert.Contains("必须填写 verification", missing);

        var ok = (await tool.InvokeAsync(new Dictionary<string, object?>
        {
            ["mode"] = "change",
            ["title"] = "重启 nginx",
            ["actions"] = "systemctl restart nginx",
            ["verification"] = "curl 200",
            ["rollback"] = "systemctl restart nginx 旧配置"
        }, default))?.ToString() ?? string.Empty;
        Assert.Contains("## 变更报告", ok);
        Assert.Contains("回滚", ok);
    }

    [Fact]
    public async Task With_memory_writes_incident_with_structured_actions()
    {
        var store = new AgentMemoryStore(_dbPath);
        var memory = new AgentMemoryService(store, new FixedEmbedder(), 5, 0.15);
        await memory.InitializeAsync();
        var tool = DiagnosisTools.Create(memory);
        Assert.False(tool.ReadOnly);

        var report = (await tool.InvokeAsync(new Dictionary<string, object?>
        {
            ["mode"] = "change",
            ["title"] = "调整 nginx 超时",
            ["assetIds"] = "ssh:web01",
            ["symptom"] = "502",
            ["evidence"] = "log_query",
            ["rootCause"] = "upstream 超时",
            ["actions"] = "调整 proxy_read_timeout",
            ["verification"] = "502 消失",
            ["impact"] = "订单应用",
            ["rollback"] = "恢复旧配置并 reload",
            ["outcome"] = "已恢复",
            ["status"] = "resolved",
            ["confidence"] = 0.8
        }, default))?.ToString() ?? string.Empty;

        Assert.Contains("## 变更报告", report);
        Assert.Contains("已记录事件", report);
        var incident = Assert.Single(await store.ListIncidentsAsync(null, null, 10));
        Assert.Equal("调整 nginx 超时", incident.Title);
        Assert.Contains("验证: 502 消失", incident.Actions);
        Assert.Contains("影响面: 订单应用", incident.Actions);
        Assert.Contains("回滚: 恢复旧配置并 reload", incident.Actions);
        Assert.Equal("ssh:web01", incident.AssetIds);
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
