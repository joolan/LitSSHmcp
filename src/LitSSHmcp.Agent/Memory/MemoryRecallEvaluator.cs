using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

public sealed record MemoryRecallEvalMemory(string Key, string Content, string Kind = "task", string AssetIds = "");

public sealed record MemoryRecallEvalCase(string Id, string Query, IReadOnlyList<string> ExpectedKeys);

public sealed record MemoryRecallEvalFile(
    IReadOnlyList<MemoryRecallEvalMemory> Memories,
    IReadOnlyList<MemoryRecallEvalCase> Cases);

public sealed record MemoryRecallEvalOptions(int TopK = 5, double Threshold = 0.15,
    int MaxRecallChars = 1500, double RelativeMargin = AgentMemoryService.DefaultRecallRelativeMargin);

public sealed record MemoryRecallEvalCaseResult(string CaseId, string Query,
    IReadOnlyList<string> RecalledKeys, IReadOnlyList<string> ExpectedKeys,
    IReadOnlyList<string> Misses, int FirstHitRank);

public sealed record MemoryRecallEvalReport(
    int Cases,
    int Memories,
    int ExpectedTotal,
    int MatchedTotal,
    double Recall,
    double Mrr,
    IReadOnlyList<MemoryRecallEvalCaseResult> CaseResults);

/// <summary>
/// 记忆召回 golden set 评测：临时库灌入 memories，逐 case 召回并与 expectedKeys 对比。
/// 可注入真实 embeddings/rerank 模型，也可用 fake 做确定性回归。
/// </summary>
public static class MemoryRecallEvaluator
{
    private static readonly Regex KeyPattern = new(
        @"\[(?:task|script|asset):([^\s×\]]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static async Task<MemoryRecallEvalReport> EvaluateAsync(
        MemoryRecallEvalFile file,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        MemoryRecallEvalOptions? options = null,
        IChatClient? reranker = null,
        CancellationToken ct = default)
    {
        options ??= new MemoryRecallEvalOptions();
        var dbPath = Path.Combine(Path.GetTempPath(), $"litssh-recall-eval-{Guid.NewGuid():N}.db");
        try
        {
            var store = new AgentMemoryStore(dbPath);
            var service = new AgentMemoryService(store, embedder, options.TopK, options.Threshold,
                maxRecallChars: options.MaxRecallChars, reranker: reranker,
                recallRelativeMargin: options.RelativeMargin);
            await service.InitializeAsync(ct);
            foreach (var memory in file.Memories)
            {
                if (string.IsNullOrWhiteSpace(memory.Key) || string.IsNullOrWhiteSpace(memory.Content))
                    continue;
                var vector = (await embedder.GenerateAsync(new[] { memory.Content }, cancellationToken: ct))
                    .First().Vector.ToArray();
                await store.AddStructuredAsync(memory.Kind ?? "task", memory.Key, "eval", memory.Content,
                    vector, memory.AssetIds ?? string.Empty, string.Empty, 1, ct);
            }

            var results = new List<MemoryRecallEvalCaseResult>();
            var expectedTotal = 0;
            var matchedTotal = 0;
            var reciprocalRanks = 0.0;
            foreach (var evalCase in file.Cases)
            {
                var expected = evalCase.ExpectedKeys
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Select(k => k.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                expectedTotal += expected.Count;
                var recalled = await service.RecallAsync(evalCase.Query, ct: ct);
                var recalledKeys = recalled is null
                    ? new List<string>()
                    : KeyPattern.Matches(recalled).Select(m => m.Groups[1].Value)
                        .Distinct(StringComparer.Ordinal).ToList();
                var misses = expected.Where(k => !recalledKeys.Contains(k, StringComparer.Ordinal)).ToList();
                var matched = expected.Count - misses.Count;
                matchedTotal += matched;
                var firstHit = expected.Count == 0
                    ? 0
                    : expected.Select((key, index) =>
                            recalledKeys.IndexOf(key) is var rank && rank >= 0 ? rank + 1 : int.MaxValue)
                        .DefaultIfEmpty(int.MaxValue).Min();
                if (expected.Count > 0 && firstHit != int.MaxValue) reciprocalRanks += 1.0 / firstHit;
                results.Add(new MemoryRecallEvalCaseResult(evalCase.Id, evalCase.Query, recalledKeys,
                    expected, misses, firstHit));
            }

            return new MemoryRecallEvalReport(file.Cases.Count, file.Memories.Count, expectedTotal,
                matchedTotal, expectedTotal == 0 ? 0 : (double)matchedTotal / expectedTotal,
                results.Count == 0 ? 0 : reciprocalRanks / results.Count, results);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(dbPath + suffix); } catch { /* ignore */ }
        }
    }

    public static MemoryRecallEvalFile LoadFile(string json)
    {
        var file = JsonSerializer.Deserialize<MemoryRecallEvalFile>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return new MemoryRecallEvalFile(file?.Memories ?? Array.Empty<MemoryRecallEvalMemory>(),
            file?.Cases ?? Array.Empty<MemoryRecallEvalCase>());
    }
}
