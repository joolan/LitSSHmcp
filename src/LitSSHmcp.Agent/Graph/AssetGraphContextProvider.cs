using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LitSSHmcp.Agent;

/// <summary>Graph-RAG 注入结果：可读上下文 + 本轮解析出的种子资产 ID（供记忆按资产过滤）。</summary>
public sealed record AssetGraphContextResult(string Text, IReadOnlyList<string> AssetIds);

/// <summary>
/// Graph-RAG：发送前从用户问题里解析资产，再通过 topology_query（action=search/neighbors）
/// 拉取 1 跳邻域，压成一段临时系统上下文。只读、失败静默，不改变工具调用链。
/// </summary>
public static class AssetGraphContextProvider
{
    private const int MaxKnownEntities = 3;
    private const int MaxCandidateTokens = 2;
    private const int MaxContextChars = 1400;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "that", "this", "what", "how", "why",
        "please", "check", "show", "list", "help", "server", "service", "services"
    };

    public static Func<string, CancellationToken, Task<AssetGraphContextResult?>>? Create(
        IAgentTool? topologyTool, string? assetGraph)
    {
        if (topologyTool is null)
            return null;

        return async (query, ct) =>
        {
            try
            {
                return await BuildAsync(topologyTool, assetGraph, query, ct);
            }
            catch
            {
                return null;
            }
        };
    }

    private static async Task<AssetGraphContextResult?> BuildAsync(
        IAgentTool topologyTool, string? assetGraph, string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var known = MatchKnownEntities(assetGraph, query);
        var seeds = new List<string>(known);
        if (seeds.Count == 0)
        {
            foreach (var token in ExtractTokens(query))
            {
                var found = await SearchAsync(topologyTool, token, ct);
                foreach (var id in found)
                    if (!seeds.Contains(id, StringComparer.Ordinal))
                        seeds.Add(id);
                if (seeds.Count >= MaxKnownEntities)
                    break;
            }
        }

        if (seeds.Count == 0)
            return null;

        var nodes = new Dictionary<string, (string Id, string Type)>(StringComparer.Ordinal);
        var edges = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var seed in seeds.Take(MaxKnownEntities))
        {
            ct.ThrowIfCancellationRequested();
            var neighborhood = await InvokeAsync(topologyTool,
                new Dictionary<string, object?> { ["action"] = "neighbors", ["assetId"] = seed, ["depth"] = 1 }, ct);
            if (neighborhood is null)
                continue;

            var root = neighborhood.Value;
            CollectNodes(Property(root, "nodes"), nodes);
            CollectNodes(Property(root, "root"), nodes);
            CollectEdges(Property(root, "edges"), edges);
        }

        if (nodes.Count == 0 && edges.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("[资产图谱上下文] 自动解析的命中资产与 1 跳邻域（只读快照，可能与最新状态有偏差；以实际工具结果为准）");
        foreach (var node in nodes.Values.Take(30))
            sb.AppendLine($"- {node.Id} ({node.Type})");
        foreach (var edge in edges.Values.Take(60))
            sb.AppendLine($"- {edge}");

        var text = sb.ToString().TrimEnd();
        if (text.Length > MaxContextChars)
            text = text[..MaxContextChars].TrimEnd() + "\n…（上下文已截断）";
        return new AssetGraphContextResult(text, seeds.Take(MaxKnownEntities).ToArray());
    }

    private static async Task<List<string>> SearchAsync(IAgentTool topologyTool, string query, CancellationToken ct)
    {
        var result = await InvokeAsync(topologyTool,
            new Dictionary<string, object?> { ["action"] = "search", ["query"] = query, ["limit"] = 5 }, ct);
        var ids = new List<string>();
        if (result is null)
            return ids;

        var items = Property(result.Value, "items");
        if (items is not { ValueKind: JsonValueKind.Array } array)
            return ids;

        foreach (var item in array.EnumerateArray())
        {
            var node = Property(item, "node");
            var id = node is { } n ? String(n, "id") : null;
            if (!string.IsNullOrWhiteSpace(id))
                ids.Add(id);
        }
        return ids;
    }

    private static async Task<JsonElement?> InvokeAsync(
        IAgentTool tool, IDictionary<string, object?> arguments, CancellationToken ct)
    {
        var result = await tool.InvokeAsync(arguments, ct);
        if (result is null)
            return null;
        if (result is JsonElement element)
            return UnwrapResult(UnwrapStructured(element));

        var json = result as string ?? JsonSerializer.Serialize(result);
        try
        {
            using var doc = JsonDocument.Parse(json);
            return UnwrapResult(UnwrapStructured(doc.RootElement.Clone()));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement UnwrapStructured(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            Property(element, "structuredContent") is { } structured)
            return structured;
        return element;
    }

    private static JsonElement UnwrapResult(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object && Property(element, "result") is { } result)
            return result.ValueKind == JsonValueKind.Null ? element : result;
        return element;
    }

    private static void CollectNodes(JsonElement? value, Dictionary<string, (string Id, string Type)> nodes)
    {
        if (value is not { } element)
            return;
        if (element.ValueKind == JsonValueKind.Object)
        {
            var id = String(element, "id");
            if (!string.IsNullOrWhiteSpace(id))
                nodes[id] = (id, String(element, "type") ?? "asset");
            return;
        }
        if (element.ValueKind != JsonValueKind.Array)
            return;
        foreach (var item in element.EnumerateArray())
            CollectNodes(item, nodes);
    }

    private static void CollectEdges(JsonElement? value, Dictionary<string, string> edges)
    {
        if (value is not { ValueKind: JsonValueKind.Array } array)
            return;
        foreach (var item in array.EnumerateArray())
        {
            var from = String(item, "from");
            var to = String(item, "to");
            var type = String(item, "type");
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(type))
                continue;
            edges[$"{from}|{type}|{to}"] = $"{from} -{type}-> {to}";
        }
    }

    private static List<string> MatchKnownEntities(string? assetGraph, string query)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(assetGraph))
            return result;

        foreach (var line in assetGraph.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("- ", StringComparison.Ordinal))
                continue;
            var parts = line[2..].Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
                continue;
            var id = parts[0];
            var label = parts.Length > 1 ? parts[1] : string.Empty;
            if ((!string.IsNullOrWhiteSpace(id) && query.Contains(id, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(label) && query.Contains(label, StringComparison.OrdinalIgnoreCase)))
            {
                if (!result.Contains(id, StringComparer.Ordinal))
                    result.Add(id);
            }
            if (result.Count >= MaxKnownEntities)
                break;
        }
        return result;
    }

    private static IEnumerable<string> ExtractTokens(string query)
    {
        return Regex.Matches(query, @"[A-Za-z0-9_.:-]{2,}")
            .Select(m => m.Value.Trim('.', ':', '-', '_'))
            .Where(t => t.Length >= 2 && !StopWords.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidateTokens);
    }

    private static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }
        return null;
    }

    private static string? String(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
