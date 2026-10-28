using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LitSSHmcp.Agent;

/// <summary>Graph-RAG 注入结果：可读上下文 + 种子资产 ID + 1 跳邻域资产 ID（供记忆按资产过滤）。</summary>
public sealed record AssetGraphContextResult(string Text, IReadOnlyList<string> AssetIds,
    IReadOnlyList<string>? RelatedAssetIds = null);

/// <summary>
/// Graph-RAG：发送前从用户问题里解析资产，再通过 topology_query（action=search/neighbors）
/// 拉取 1 跳邻域，压成一段临时系统上下文。只读、失败静默，不改变工具调用链。
/// </summary>
public static class AssetGraphContextProvider
{
    private const int MaxKnownEntities = 3;
    private const int MaxContextChars = 1400;

    /// <summary>显式资产 ID（ssh:/ds:/app:/service:/container:/domain:/cert: 前缀）。</summary>
    private static readonly Regex ExplicitAssetId = new(
        @"(?i)\b(?:ssh|ds|app|service|container|domain|cert):[A-Za-z0-9._-]+", RegexOptions.Compiled);

    public static Func<string, CancellationToken, Task<AssetGraphContextResult?>>? Create(
        IAgentTool? topologyTool, string? assetGraph)
    {
        if (topologyTool is null)
            return null;

        // 失败/超时由调用方（AgentSession）统一捕获并推 Warning 事件，避免静默丢失可观测性。
        return (query, ct) => BuildAsync(topologyTool, assetGraph, query, ct);
    }

    private static async Task<AssetGraphContextResult?> BuildAsync(
        IAgentTool topologyTool, string? assetGraph, string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        // 只使用本地资产清单命中或问题中显式写出的资产 ID；不再对普通词做 topology search，
        // 避免每个请求都额外打一次 MCP（这也是本轮 Graph-RAG 门控的核心）。
        var seeds = ResolveSeeds(assetGraph, query);
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
        var related = nodes.Keys
            .Where(id => !seeds.Contains(id, StringComparer.Ordinal))
            .Take(10)
            .ToArray();
        return new AssetGraphContextResult(text, seeds.Take(MaxKnownEntities).ToArray(), related);
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

    /// <summary>从本地资产清单中匹配问题里出现的资产 ID/名称（纯本地字符串匹配，无 MCP 调用）。</summary>
    public static IReadOnlyList<string> MatchKnownEntities(string? assetGraph, string? query)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(assetGraph) || string.IsNullOrWhiteSpace(query))
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

    /// <summary>问题中显式写出的资产 ID（如 ssh:web-01 / ds:mysql-01 / app:order）。</summary>
    public static IReadOnlyList<string> ExtractExplicitAssetIds(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        return ExplicitAssetId.Matches(query)
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxKnownEntities)
            .ToArray();
    }

    /// <summary>本轮 Graph-RAG 的种子：本地清单命中 + 问题中显式写出的资产 ID（最多 3 个）。</summary>
    public static IReadOnlyList<string> ResolveSeeds(string? assetGraph, string? query)
    {
        var seeds = new List<string>(MatchKnownEntities(assetGraph, query));
        foreach (var id in ExtractExplicitAssetIds(query))
            if (!seeds.Contains(id, StringComparer.Ordinal))
                seeds.Add(id);
        return seeds.Take(MaxKnownEntities).ToArray();
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
