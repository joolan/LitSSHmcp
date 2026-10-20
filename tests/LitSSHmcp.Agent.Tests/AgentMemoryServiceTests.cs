using LitSSHmcp.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class AgentMemoryServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"litssh-memtest-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* 忽略 */ }
        }
    }

    private async Task<(AgentMemoryService Svc, AgentMemoryStore Store)> CreateAsync(int topK = 5, double threshold = 0.15)
    {
        var store = new AgentMemoryStore(_dbPath);
        var svc = new AgentMemoryService(store, new FakeEmbedder(), topK, threshold);
        await svc.InitializeAsync();
        return (svc, store);
    }

    [Fact]
    public async Task Save_rejects_sensitive_content()
    {
        var (svc, _) = await CreateAsync();
        var result = await svc.SaveAsync("task", "k1", "password=supersecret123 处理磁盘满", null, null);
        Assert.StartsWith("错误:", result);
        Assert.Contains("敏感", result);
    }

    [Fact]
    public async Task Save_rejects_too_short_content()
    {
        var (svc, _) = await CreateAsync();
        var result = await svc.SaveAsync("task", "k1", "短", null, null);
        Assert.StartsWith("错误:", result);
        Assert.Contains("过短", result);
    }

    [Fact]
    public async Task Save_adds_then_updates_same_key_freq_plus_one()
    {
        var (svc, store) = await CreateAsync();
        var r1 = await svc.SaveAsync("task", "磁盘满:ssh:web01", "现象: /var 满; 根因: 日志; 处置: truncate", "ssh:web01", null);
        Assert.Contains("已新增", r1);
        var r2 = await svc.SaveAsync("task", "磁盘满:ssh:web01", "现象: /var 满; 根因: journal; 处置: vacuum", "ssh:web01", null);
        Assert.Contains("已更新", r2);
        Assert.Contains("频次 2", r2);
        var rec = await store.FindByKeyAsync("task", "磁盘满:ssh:web01");
        Assert.NotNull(rec);
        Assert.Equal(2, rec!.Freq);
    }

    [Fact]
    public async Task Save_merges_near_duplicate_under_different_key()
    {
        var (svc, store) = await CreateAsync();
        var body = "现象: /var 分区 100%; 根因: journald 日志膨胀; 处置: journalctl --vacuum-size=200M 验证: df 恢复";
        await svc.SaveAsync("task", "磁盘满:ssh:web01", body, "ssh:web01", null);
        // 相同/近似内容 + 不同 key → 近重复合并
        var result = await svc.SaveAsync("task", "磁盘满备选:ssh:web01", body, null, null);
        Assert.Contains("已合并近重复", result);
        Assert.Null(await store.FindByKeyAsync("task", "磁盘满备选:ssh:web01"));
        var rec = await store.FindByKeyAsync("task", "磁盘满:ssh:web01");
        Assert.Equal(2, rec!.Freq);
    }

    [Fact]
    public async Task Recall_prefers_structured_and_caps_raw()
    {
        var (svc, _) = await CreateAsync(topK: 5);
        // 3 条结构化（内容足够区分, 避免触发近重复合并）+ 4 条 raw, 内容都和查询相关
        await svc.SaveAsync("task", "exp0", "磁盘满经验甲: journalctl --vacuum-size 清理 journald 日志后 df 恢复正常", "ssh:s0", null);
        await svc.SaveAsync("task", "exp1", "磁盘满经验乙: /var/log/app 未轮转导致占满, 配 logrotate 后恢复", "ssh:s1", null);
        await svc.SaveAsync("task", "exp2", "磁盘满经验丙: docker 系统盘镜像堆积, docker system prune 释放空间", "ssh:s2", null);
        for (var i = 0; i < 4; i++)
            await svc.IndexAsync("会话", $"磁盘满 排查第{i}轮: 用户问为什么磁盘满, 助手查 df 定位分区");

        var recalled = await svc.RecallAsync("磁盘满怎么处理");
        Assert.NotNull(recalled);
        var structuredCount = recalled!.Split('\n').Count(l => l.Contains("task:"));
        var rawCount = recalled.Split('\n').Count(l => l.StartsWith("- [会话]", StringComparison.Ordinal));
        Assert.Equal(3, structuredCount);
        Assert.True(rawCount <= 2, $"raw 条数 {rawCount} 应 ≤ 2");
    }

    [Fact]
    public async Task Recall_respects_threshold()
    {
        var (svc, _) = await CreateAsync(topK: 5, threshold: 0.99);   // 极高阈值: 无关内容不召回
        await svc.SaveAsync("task", "k", "磁盘满处理经验: journal vacuum 修复", null, null);
        var recalled = await svc.RecallAsync("完全无关的天气查询内容今天");
        Assert.Null(recalled);
    }

    [Fact]
    public async Task Recall_filters_asset_specific_memories_but_keeps_generic()
    {
        var (svc, _) = await CreateAsync(topK: 5);
        await svc.SaveAsync("task", "disk:s1", "磁盘满经验甲: journalctl --vacuum-size 清理 journald 日志后 df 恢复", "ssh:s1", null);
        await svc.SaveAsync("task", "nginx:s2", "磁盘满相关噪声乙: nginx upstream 超时, 调整 proxy_read_timeout 后恢复", "ssh:s2", null);
        await svc.SaveAsync("task", "disk:generic", "磁盘满通用经验: 先 df 定位分区再清理日志", null, null);

        var recalled = await svc.RecallAsync("磁盘满 日志 怎么处理", assetIds: new[] { "ssh:s1" });

        Assert.NotNull(recalled);
        Assert.Contains("task:disk:s1", recalled);
        Assert.Contains("task:disk:generic", recalled);
        Assert.DoesNotContain("task:nginx:s2", recalled);
    }

    [Fact]
    public async Task Recall_asset_boundary_does_not_match_prefix()
    {
        var (svc, _) = await CreateAsync(topK: 5);
        await svc.SaveAsync("task", "disk:s10", "磁盘满经验: journalctl --vacuum-size 清理日志后恢复", "ssh:s10", null);

        var recalled = await svc.RecallAsync("磁盘满 日志", assetIds: new[] { "ssh:s1" });

        Assert.Null(recalled);
    }

    [Fact]
    public async Task Recall_asset_boundary_escapes_like_wildcards()
    {
        var (svc, _) = await CreateAsync(topK: 5);
        await svc.SaveAsync("task", "disk:web-1", "磁盘满经验甲: journalctl --vacuum-size 清理日志后恢复", "ssh:web_1", null);
        await svc.SaveAsync("task", "disk:web01", "磁盘满经验乙: 清理旧内核与缓存后恢复", "ssh:web01", null);

        var recalled = await svc.RecallAsync("磁盘满 日志", assetIds: new[] { "ssh:web_1" });

        Assert.NotNull(recalled);
        Assert.Contains("task:disk:web-1", recalled);
        Assert.DoesNotContain("task:disk:web01", recalled);
    }

    [Fact]
    public async Task PruneRaw_keeps_only_latest_n()
    {
        var (svc, store) = await CreateAsync();
        for (var i = 0; i < 10; i++)
            await svc.IndexAsync("会话", $"第{i}轮: 磁盘满排查 journal 处理经验内容足够长以入库{i}");
        var deleted = await svc.PruneRawAsync("会话", 4);
        Assert.Equal(6, deleted);
        var remaining = await store.ListAsync(null, null, 100, includeRaw: true);
        Assert.Equal(4, remaining.Count);
    }

    [Fact]
    public async Task IndexTurn_compresses_log_dump()
    {
        var (svc, store) = await CreateAsync();
        var logLines = string.Join("\n", Enumerable.Range(0, 30).Select(i =>
            $"2026-10-16 12:00:{i:D2} ERROR java.lang.OutOfMemoryError at /opt/app/logs/app.log line{i}"));
        var content = MemoryQuality.BuildRawContent(logLines, "结论: 堆内存不足, 建议 -Xmx 调大并重启验证。");
        Assert.NotNull(content);
        Assert.True(content!.Length <= MemoryQuality.MaxCompressedLogChars + 80);
        Assert.Contains("结论", content);
        Assert.Contains("/opt/app", content);
        // 30 行日志原文不应整体入库
        Assert.DoesNotContain("line29", content);
    }

    [Fact]
    public void ContainsSensitive_detects_common_secret_shapes()
    {
        Assert.True(MemoryQuality.ContainsSensitive("password=hunter2"));
        Assert.True(MemoryQuality.ContainsSensitive("api_key = 'abcd1234efgh'"));
        Assert.True(MemoryQuality.ContainsSensitive("-----BEGIN RSA PRIVATE KEY-----"));
        Assert.True(MemoryQuality.ContainsSensitive("token: ghp_abcdefghijklmnopqrstuvwxyz012345"));
        Assert.True(MemoryQuality.ContainsSensitive("AKIAIOSFODNN7EXAMPLE"));
        Assert.False(MemoryQuality.ContainsSensitive("磁盘满: journalctl --vacuum-size=200M"));
    }

    [Fact]
    public void BuildRawContent_short_turn_keeps_full_text()
    {
        var content = MemoryQuality.BuildRawContent("磁盘满怎么办", "先 df 定位再处理 /var");
        Assert.Equal("用户: 磁盘满怎么办\n助手: 先 df 定位再处理 /var", content);
    }

    /// <summary>确定性假 embedder：相同文本 → 相同向量；基于字符 n-gram 的重叠度近似语义相似。</summary>
    private sealed class FakeEmbedder : IEmbeddingGenerator<string, Embedding<float>>, IDisposable
    {
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = values.Select(v => new Embedding<float>(Hash(v))).ToList();
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
        }

        private static float[] Hash(string text)
        {
            // 256 维字符 unigram/hashed-bucket 向量: 相同字符分布 → 高余弦
            var v = new float[256];
            foreach (var c in text.ToLowerInvariant())
                v[(byte)c % 256] += 1f;
            var norm = MathF.Sqrt(v.Sum(x => x * x));
            if (norm > 0)
                for (var i = 0; i < v.Length; i++) v[i] /= norm;
            return v;
        }
    }
}
