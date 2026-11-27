using System.Text.Json;
using System.Text.Json.Nodes;

namespace LitSSHmcp.Agent;

/// <summary>
/// 从工具调用参数里识别本轮真正触达的资产：<c>serverId / serverIds / assetId / dataSourceId / deployedTo</c> 等
/// 字段里可能是资产 ID、名称或主机名，统一解析成 <c>ssh:...</c>/<c>ds:...</c> 等规范 ID。
/// 记忆写入时用它做资产归属，比只从自然语言里找服务器名更可靠。
/// </summary>
public static class AssetAttribution
{
    public const int MaxAssets = 5;

    private static readonly string[] TargetKeyHints =
    {
        "server", "asset", "target", "datasource", "data_source", "database", "dbid",
        "deployed", "host", "instance", "source", "destination"
    };

    /// <summary>扫描一次工具调用的参数；没有可识别的目标资产时返回空。</summary>
    public static IReadOnlyList<string> FromArguments(
        string? assetGraph, IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return Array.Empty<string>();

        var resolver = AssetIdResolver.FromPrompt(assetGraph);
        var found = new List<string>(MaxAssets);
        foreach (var (key, value) in arguments)
        {
            if (!IsTargetKey(key))
                continue;
            Scan(value, resolver, found, depth: 1);
        }
        return found;
    }

    private static bool IsTargetKey(string key)
    {
        var normalized = NormalizeKey(key);
        return normalized.Length > 0 &&
               TargetKeyHints.Any(hint => normalized.Contains(NormalizeKey(hint), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeKey(string value) =>
        value.Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);

    private static void Scan(object? value, AssetIdResolver resolver, List<string> found, int depth)
    {
        if (value is null || found.Count >= MaxAssets || depth > 6)
            return;

        switch (value)
        {
            case string text:
                foreach (var token in Tokenize(text))
                    Add(token, resolver, found);
                return;
            case JsonElement element:
                ScanJson(element, resolver, found, depth);
                return;
            case JsonNode node:
                ScanJsonNode(node, resolver, found, depth);
                return;
            case IDictionary<string, object?> map:
                foreach (var (key, nested) in map)
                {
                    // 顶层只认“目标类”字段；进入目标字段内部后，允许按值继续找 id/name。
                    if (depth <= 1 && !IsTargetKey(key))
                        continue;
                    Scan(nested, resolver, found, depth + 1);
                }
                return;
            case System.Collections.IEnumerable items:
                foreach (var item in items)
                    Scan(item, resolver, found, depth + 1);
                return;
            default:
                Add(value.ToString(), resolver, found);
                return;
        }
    }

    private static void ScanJson(JsonElement element, AssetIdResolver resolver, List<string> found, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                foreach (var token in Tokenize(element.GetString() ?? string.Empty))
                    Add(token, resolver, found);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    ScanJson(item, resolver, found, depth + 1);
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (depth <= 1 && !IsTargetKey(property.Name))
                        continue;
                    ScanJson(property.Value, resolver, found, depth + 1);
                }
                break;
        }
    }

    private static void ScanJsonNode(JsonNode node, AssetIdResolver resolver, List<string> found, int depth)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                foreach (var token in Tokenize(text ?? string.Empty))
                    Add(token, resolver, found);
                break;
            case JsonArray array:
                foreach (var item in array)
                    if (item is not null)
                        ScanJsonNode(item, resolver, found, depth + 1);
                break;
            case JsonObject obj:
                foreach (var (key, nested) in obj)
                {
                    if (nested is null || (depth <= 1 && !IsTargetKey(key)))
                        continue;
                    ScanJsonNode(nested, resolver, found, depth + 1);
                }
                break;
        }
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (var token in text.Split(
                     new[] { ',', ';', '，', '；', '、', ' ', '\t', '\r', '\n', '|' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return token;
        }
    }

    private static void Add(string? token, AssetIdResolver resolver, List<string> found)
    {
        if (found.Count >= MaxAssets || string.IsNullOrWhiteSpace(token))
            return;
        var asset = resolver.Resolve(token);
        if (asset is not null && !found.Contains(asset, StringComparer.OrdinalIgnoreCase))
            found.Add(asset);
    }
}

/// <summary>
/// 把资产清单（系统提示里的 <c>- ssh:... | 名称 | host:port</c>）解析成「ID/名称/主机名 → 资产 ID」的解析器。
/// 显式带前缀的 ID（如 <c>ssh:abc</c>）即使不在清单里也原样保留，避免清单被截断时丢归属。
/// </summary>
public sealed class AssetIdResolver
{
    private static readonly string[] AssetPrefixes =
    {
        "ssh", "ds", "app", "service", "container", "domain", "cert", "mq", "port"
    };

    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public static AssetIdResolver FromPrompt(string? assetGraph)
    {
        var resolver = new AssetIdResolver();
        if (string.IsNullOrWhiteSpace(assetGraph))
            return resolver;

        foreach (var rawLine in assetGraph.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("- ", StringComparison.Ordinal))
                continue;

            var parts = line[2..].Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || !LooksLikeAssetId(parts[0]))
                continue;

            var id = parts[0];
            resolver.Add(id, id);
            var separator = id.IndexOf(':');
            if (separator > 0)
                resolver.Add(id[(separator + 1)..], id);

            if (parts.Length > 1)
            {
                var label = parts[1].Trim();
                resolver.Add(label, id);
                var labelParts = label.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (labelParts.Length == 2)
                    resolver.Add(labelParts[1], id);
            }

            if (parts.Length > 2)
            {
                var endpoint = parts[2].Trim();
                resolver.Add(endpoint, id);
                var colon = endpoint.LastIndexOf(':');
                if (colon > 0)
                    resolver.Add(endpoint[..colon], id);
            }
        }
        return resolver;
    }

    /// <summary>解析一个 token；无前缀的不在清单中时返回 null，避免把普通命令词当成资产。</summary>
    public string? Resolve(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var value = token.Trim().Trim('"', '\'', '`', '[', ']', '(', ')', '{', '}', '<', '>', ',', ';', '，', '；');
        if (value.Length == 0)
            return null;
        if (LooksLikeAssetId(value))
            return value;
        return _map.TryGetValue(value, out var id) ? id : null;
    }

    private void Add(string? value, string id)
    {
        if (!string.IsNullOrWhiteSpace(value))
            _map[value.Trim()] = id;
    }

    private static bool LooksLikeAssetId(string value)
    {
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
            return false;
        var prefix = value[..separator];
        return AssetPrefixes.Contains(prefix, StringComparer.OrdinalIgnoreCase);
    }
}
