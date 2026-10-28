using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 可选的独立 rerank 模型：对已召回的少量候选按查询相关性重排。
/// 模型失败/超时/返回非法 JSON 时原样返回，绝不阻塞主召回。
/// </summary>
public sealed class MemoryReranker
{
    private const int MaxCandidates = 10;
    private static readonly Regex NumberPattern = new(@"\d+", RegexOptions.Compiled);
    private readonly IChatClient _client;

    public MemoryReranker(IChatClient client) => _client = client;

    public async Task<List<MemoryHit>> RerankAsync(string query, IReadOnlyList<MemoryHit> hits,
        CancellationToken ct = default)
    {
        if (hits.Count <= 1)
            return hits.ToList();
        var candidates = hits.Take(MaxCandidates).ToList();
        var prompt = BuildPrompt(query, candidates);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var response = await _client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System,
                    "你是运维记忆重排器。只返回 JSON：{\"order\":[候选编号...]}，按与查询的相关性从高到低；不要解释。"),
                new ChatMessage(ChatRole.User, prompt)
            ], cancellationToken: timeout.Token);
            var order = ParseOrder(response.Text);
            if (order.Count == 0)
                return hits.ToList();
            var selected = new HashSet<int>();
            var result = new List<MemoryHit>(hits.Count);
            foreach (var index in order)
            {
                if (index < 1 || index > candidates.Count || !selected.Add(index))
                    continue;
                result.Add(candidates[index - 1]);
            }
            foreach (var hit in hits)
            {
                if (!result.Contains(hit)) result.Add(hit);
            }
            return result;
        }
        catch
        {
            return hits.ToList();
        }
    }

    private static string BuildPrompt(string query, IReadOnlyList<MemoryHit> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("查询:");
        sb.AppendLine(Truncate(query, 500));
        sb.AppendLine("候选:");
        for (var i = 0; i < candidates.Count; i++)
            sb.AppendLine($"[{i + 1}] {Truncate(candidates[i].Content, 400)}");
        sb.AppendLine("输出 JSON order。");
        return sb.ToString();
    }

    private static List<int> ParseOrder(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<int>();
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("order", out var order) &&
                order.ValueKind == JsonValueKind.Array)
                return order.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.Number)
                    .Select(x => x.GetInt32())
                    .ToList();
        }
        catch
        {
            // 兼容模型夹带解释：从文本中提取数字作为兜底。
        }
        return NumberPattern.Matches(text).Select(m => int.Parse(m.Value)).Take(MaxCandidates).ToList();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max];
}
