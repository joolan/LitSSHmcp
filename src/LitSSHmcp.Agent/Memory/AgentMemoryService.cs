using System.Text;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>长期记忆服务：向量化并索引文本，按查询召回相关记忆片段。</summary>
public sealed class AgentMemoryService
{
    private readonly IAgentMemoryStore _store;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly int _topK;

    public AgentMemoryService(IAgentMemoryStore store, IEmbeddingGenerator<string, Embedding<float>> embedder, int topK)
    {
        _store = store;
        _embedder = embedder;
        _topK = Math.Max(1, topK);
    }

    public Task InitializeAsync(CancellationToken ct = default) => _store.InitializeAsync(ct);
    public Task<int> CountAsync(CancellationToken ct = default) => _store.CountAsync(ct);

    /// <summary>索引一段文本（source 用于展示来源；相同内容不重复索引）。</summary>
    public async Task IndexAsync(string source, string content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;
        if (await _store.ExistsAsync(source, content, ct))
            return;

        var vector = await EmbedAsync(content, ct);
        await _store.AddAsync(source, content, vector, ct);
    }

    /// <summary>按查询召回 topK 记忆，拼成可直接注入提示的文本；无相关记忆返回 null。</summary>
    public async Task<string?> RecallAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var vector = await EmbedAsync(query, ct);
        var hits = (await _store.SearchAsync(vector, _topK, ct)).Where(h => h.Score > 0.15).ToList();
        if (hits.Count == 0)
            return null;

        var sb = new StringBuilder();
        foreach (var hit in hits)
            sb.AppendLine($"- [{hit.Source}] {Truncate(hit.Content, 500)}");
        return sb.ToString().Trim();
    }

    private async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var result = await _embedder.GenerateAsync(new[] { text }, cancellationToken: ct);
        return result[0].Vector.ToArray();
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
