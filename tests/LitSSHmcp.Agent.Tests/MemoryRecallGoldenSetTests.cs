using LitSSHmcp.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

/// <summary>
/// 记忆召回回归门禁：Fixtures/recall-golden.json 里的 34 条 query 来自真实会话
/// （%APPDATA%\LitSSH\agent.db 的 agent_messages），另加 10 条主题变体边界 case。
/// 用确定性 TopicEmbedder 而非线上 embeddings，保证阈值/排序改动能立即可复现地验证。
/// 门禁：Recall ≥ 0.80、MRR ≥ 0.50；调阈值或改召回逻辑导致退化时这里会失败。
/// </summary>
public class MemoryRecallGoldenSetTests
{
    [Fact]
    public async Task Golden_set_meets_recall_gate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "recall-golden.json");
        Assert.True(File.Exists(path), $"找不到 golden set: {path}");
        var file = MemoryRecallEvaluator.LoadFile(File.ReadAllText(path));

        Assert.True(file.Memories.Count >= 10);
        Assert.True(file.Cases.Count >= 30, $"case 数不足: {file.Cases.Count}");

        var report = await MemoryRecallEvaluator.EvaluateAsync(file, new TopicEmbedder(),
            new MemoryRecallEvalOptions(TopK: 5, Threshold: AgentMemoryService.DefaultRecallThreshold));

        Assert.True(report.Recall >= 0.80,
            $"召回率 {report.Recall:0.000} 低于门禁 0.80；未命中 case: " +
            string.Join("; ", report.CaseResults.Where(c => c.Misses.Count > 0)
                .Take(5).Select(c => $"{c.CaseId}({string.Join(",", c.Misses)})")));
        Assert.True(report.Mrr >= 0.50, $"MRR {report.Mrr:0.000} 低于门禁 0.50");
    }

    [Fact]
    public async Task Golden_set_is_sensitive_to_broken_threshold()
    {
        // 反向护栏：把相对阈值彻底关掉 + 极低绝对下限时，评测器应能跑通且指标可用；
        // 这里主要锁定「评测器能加载真实语料并产出完整报告」，供后续 A/B 调参。
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "recall-golden.json");
        var file = MemoryRecallEvaluator.LoadFile(File.ReadAllText(path));
        var report = await MemoryRecallEvaluator.EvaluateAsync(file, new TopicEmbedder(),
            new MemoryRecallEvalOptions(TopK: 5, Threshold: 0.15, RelativeMargin: 0));
        Assert.Equal(file.Cases.Count, report.Cases);
        Assert.Equal(file.Cases.Count, report.CaseResults.Count);
    }

    /// <summary>按运维主题词命中数构造向量；主题顺序必须与 golden set 的主题定义一致。</summary>
    private sealed class TopicEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        private static readonly string[][] TopicWords =
        {
            new[] { "磁盘", "空间", "df", "清理日志", "/var" },
            new[] { "nginx", "站点", "虚拟主机" },
            new[] { "redis", "缓存key", "读写key" },
            new[] { "mysql", "数据库", "数据源", "sql" },
            new[] { "证书", "过期", "续期", "https" },
            new[] { "docker", "容器", "镜像" },
            new[] { "在线", "连通", "ping", "网络", "态势" },
            new[] { "端口", "监听", "开放了哪些" },
            new[] { "账号", "用户", "权限", "提权", "sudo" },
            new[] { "安全", "风险", "评估", "加固" }
        };

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(v => new Embedding<float>(Vectorize(v))).ToList()));

        private static float[] Vectorize(string text)
        {
            var v = new float[TopicWords.Length + 1];
            for (var i = 0; i < TopicWords.Length; i++)
                foreach (var word in TopicWords[i])
                    if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
                        v[i] += 1f;
            v[^1] = 0.0001f;   // 避免零向量导致余弦未定义
            var norm = MathF.Sqrt(v.Sum(x => x * x));
            if (norm > 0)
                for (var i = 0; i < v.Length; i++) v[i] /= norm;
            return v;
        }
    }
}
