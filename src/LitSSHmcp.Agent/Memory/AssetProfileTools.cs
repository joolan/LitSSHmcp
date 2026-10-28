using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 资产画像：图谱 1 跳邻域/影响面 + 该资产及邻域的事实、历史事件、脚本经验。
/// 只读；用于“这台机器/应用以前出过什么、改它会影响谁”的影响面诊断与交接。
/// </summary>
public static class AssetProfileTools
{
    public const string ToolName = "ops_asset_profile";

    public static IAgentTool Create(IAgentMemoryStore? memory, IAgentTool? topologyTool)
    {
        var function = new LocalFunction(
            ToolName,
            "查看资产画像：图谱 1 跳邻域/影响面 + 资产事实/历史事件/脚本经验；用于影响面分析、排障前查历史与交接。assetId 可用 ssh:/ds:/app:/service:/container:/domain:/cert: 前缀。",
            """{"type":"object","properties":{"assetId":{"type":"string","description":"资产ID, 如 ssh:web01 / app:order-service / cert:..."},"limit":{"type":"integer","description":"每类记忆最多条数, 默认10, 上限50"}},"required":["assetId"]}""",
            async (args, ct) =>
            {
                var assetId = (ArgumentReader.ReadString(args, "assetId") ?? string.Empty).Trim();
                if (assetId.Length == 0)
                    return "错误: assetId 为必填";
                var limit = Math.Clamp(ArgumentReader.ReadInt(args, "limit") ?? 10, 1, 50);

                var sb = new StringBuilder();
                sb.AppendLine($"## 资产画像 {assetId}");
                var related = new List<string>();

                if (topologyTool is not null)
                {
                    var neighbors = await InvokeAsync(topologyTool,
                        new Dictionary<string, object?> { ["action"] = "neighbors", ["assetId"] = assetId, ["depth"] = 1 }, ct);
                    if (neighbors is not null)
                    {
                        AppendNeighbors(sb, neighbors.Value);
                        related.AddRange(ExtractNodeIds(neighbors.Value));
                    }

                    var impact = await InvokeAsync(topologyTool,
                        new Dictionary<string, object?> { ["action"] = "impact", ["assetId"] = assetId, ["maxDepth"] = 6 }, ct);
                    if (impact is not null)
                        AppendImpact(sb, impact.Value);
                }

                if (memory is not null)
                {
                    var ids = new[] { assetId }
                        .Concat(related)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(20)
                        .ToList();
                    await AppendMemoryAsync(sb, memory, ids, limit, ct);
                }

                if (sb.ToString().Trim() == $"## 资产画像 {assetId}")
                    sb.AppendLine("(图谱与记忆均无可用信息；请先确认资产 ID 或执行 topology_discover/快照采集)");
                return sb.ToString().Trim();
            });

        return new LocalAgentTool(function, readOnly: true);
    }

    private static void AppendNeighbors(StringBuilder sb, JsonElement root)
    {
        sb.AppendLine("### 图谱（1 跳邻域）");
        var rootNode = Property(root, "root");
        if (rootNode is { } r)
            sb.AppendLine($"- 根节点: {String(r, "id")} ({String(r, "type")}) {String(r, "label")}".Trim());
        if (Property(root, "nodes") is { ValueKind: JsonValueKind.Array } nodes)
        {
            foreach (var node in nodes.EnumerateArray().Take(20))
                sb.AppendLine($"- 邻居: {String(node, "id")} ({String(node, "type")}) {String(node, "label")}".Trim());
        }
        if (Property(root, "edges") is { ValueKind: JsonValueKind.Array } edges)
        {
            foreach (var edge in edges.EnumerateArray().Take(30))
                sb.AppendLine($"- 关系: {String(edge, "from")} -{String(edge, "type")}-> {String(edge, "to")}");
        }
    }

    private static void AppendImpact(StringBuilder sb, JsonElement root)
    {
        sb.AppendLine("### 影响面（谁依赖它）");
        if (Property(root, "impacted") is not { ValueKind: JsonValueKind.Array } impacted)
            return;
        var count = 0;
        foreach (var item in impacted.EnumerateArray())
        {
            var node = Property(item, "node");
            if (node is not { } n)
                continue;
            sb.AppendLine($"- {String(n, "id")} ({String(n, "type")}) {String(n, "label")}".Trim());
            if (++count >= 20)
                break;
        }
        if (count == 0)
            sb.AppendLine("(未发现反向依赖；可能没有下游应用/域名/端口)");
    }

    private static async Task AppendMemoryAsync(StringBuilder sb, IAgentMemoryStore memory,
        IReadOnlyList<string> assetIds, int limit, CancellationToken ct)
    {
        var seenIncidents = new HashSet<long>();
        var seenMemories = new HashSet<long>();
        var wroteAny = false;
        foreach (var assetId in assetIds)
        {
            var incidents = await memory.ListIncidentsAsync(assetId, null, Math.Max(2, limit / 2), ct);
            var incidentRows = incidents.Where(i => seenIncidents.Add(i.Id)).ToList();
            if (incidentRows.Count > 0)
            {
                sb.AppendLine("### 历史事件");
                wroteAny = true;
                foreach (var incident in incidentRows)
                {
                    sb.AppendLine($"- #{incident.Id} [{incident.Status}] {incident.Title} | 资产={assetId}" +
                                  (string.IsNullOrWhiteSpace(incident.RootCause) ? string.Empty : $" | 根因: {Truncate(incident.RootCause, 200)}"));
                }
            }

            var facts = await memory.ListByAssetAsync(assetId, "asset", limit, ct);
            var factRows = facts.Where(f => seenMemories.Add(f.Id)).ToList();
            if (factRows.Count > 0)
            {
                sb.AppendLine($"### 资产事实 · {assetId}");
                wroteAny = true;
                foreach (var fact in factRows)
                    sb.AppendLine($"- [{fact.Key}] {Truncate(fact.Content, 500)}");
            }

            var scripts = await memory.ListByAssetAsync(assetId, "script", limit, ct);
            var scriptRows = scripts.Where(s => seenMemories.Add(s.Id)).ToList();
            if (scriptRows.Count > 0)
            {
                sb.AppendLine($"### 脚本经验 · {assetId}");
                wroteAny = true;
                foreach (var script in scriptRows)
                    sb.AppendLine($"- [{script.Key}] {Truncate(script.Content, 500)}");
            }
        }
        if (!wroteAny)
        {
            sb.AppendLine("### 记忆");
            sb.AppendLine("(暂无该资产及 1 跳邻域的事实/事件/脚本)");
        }
    }

    private static async Task<JsonElement?> InvokeAsync(IAgentTool tool,
        IDictionary<string, object?> arguments, CancellationToken ct)
    {
        var result = await tool.InvokeAsync(arguments, ct);
        if (result is null)
            return null;
        if (result is JsonElement element)
            return Unwrap(UnwrapStructured(element));
        var json = result as string ?? JsonSerializer.Serialize(result);
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Unwrap(UnwrapStructured(doc.RootElement.Clone()));
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement UnwrapStructured(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && Property(element, "structuredContent") is { } structured
            ? structured
            : element;

    private static JsonElement Unwrap(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && Property(element, "result") is { } result &&
        result.ValueKind != JsonValueKind.Null
            ? result
            : element;

    private static List<string> ExtractNodeIds(JsonElement root)
    {
        var ids = new List<string>();
        if (Property(root, "root") is { } r && String(r, "id") is { } rootId)
            ids.Add(rootId);
        if (Property(root, "nodes") is { ValueKind: JsonValueKind.Array } nodes)
        {
            foreach (var node in nodes.EnumerateArray())
                if (String(node, "id") is { } id)
                    ids.Add(id);
        }
        return ids;
    }

    private static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return null;
    }

    private static string? String(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
