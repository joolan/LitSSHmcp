using LitSSHmcp.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class MemoryRecallEvaluatorTests
{
    [Fact]
    public async Task Golden_set_reports_recall_and_misses()
    {
        var file = new MemoryRecallEvalFile(
            new[]
            {
                new MemoryRecallEvalMemory("disk", "磁盘满时清理 journald 并检查 df"),
                new MemoryRecallEvalMemory("nginx", "nginx upstream timeout 调整 proxy_read_timeout")
            },
            new[]
            {
                new MemoryRecallEvalCase("c1", "磁盘满", new[] { "disk" }),
                new MemoryRecallEvalCase("c2", "nginx 超时", new[] { "nginx" }),
                new MemoryRecallEvalCase("c3", "磁盘满", new[] { "missing" })
            });

        var report = await MemoryRecallEvaluator.EvaluateAsync(file, new KeywordEmbedder());

        Assert.Equal(3, report.Cases);
        Assert.Equal(2, report.MatchedTotal);
        Assert.Equal(2.0 / 3, report.Recall, 3);
        Assert.Contains(report.CaseResults, c => c.CaseId == "c3" && c.Misses.Contains("missing"));
    }

    private sealed class KeywordEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = values.Select(value => new Embedding<float>(value.Contains("磁盘", StringComparison.Ordinal)
                ? new[] { 1f, 0f } : new[] { 0f, 1f })).ToList();
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
        }

        public void Dispose() { }
    }
}
