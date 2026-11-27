using System.Text;

namespace LitSSHmcp.Agent;

/// <summary>
/// 把资产 ID 渲染成“名称 (主机) [ID]”，用于记忆召回、记忆列表和事件记录展示。
/// 资产清单来自系统提示里的 <c>- ssh:... | 名称 | host:port</c>；清单里没有的 ID 原样保留。
/// </summary>
public sealed class AssetLabelMap
{
    private readonly Dictionary<string, string> _labels = new(StringComparer.OrdinalIgnoreCase);

    public static AssetLabelMap FromPrompt(string? assetGraph)
    {
        var map = new AssetLabelMap();
        if (string.IsNullOrWhiteSpace(assetGraph))
            return map;

        foreach (var rawLine in assetGraph.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("- ", StringComparison.Ordinal))
                continue;

            var parts = line[2..].Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
                continue;

            var id = parts[0].Trim();
            if (id.Length == 0)
                continue;

            var label = parts.Length > 1 ? parts[1].Trim() : string.Empty;
            var endpoint = parts.Length > 2 ? parts[2].Trim() : string.Empty;
            // 数据源行可能是 "10.0.0.3:3306 db=orders"，只取端点部分。
            var space = endpoint.IndexOf(' ');
            if (space > 0)
                endpoint = endpoint[..space];

            map._labels[id] = Build(id, label, endpoint);
        }
        return map;
    }

    /// <summary>返回“名称 (主机) [ID]”；无标签时返回 null（由调用方决定是否只显示 ID）。</summary>
    public string? Describe(string? assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId))
            return null;
        var value = assetId.Trim();
        return _labels.TryGetValue(value, out var label) ? label : null;
    }

    /// <summary>渲染逗号分隔的资产列表；未知 ID 保留原样，避免丢失可操作信息。</summary>
    public string DescribeList(string? assetIds)
    {
        if (string.IsNullOrWhiteSpace(assetIds))
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var id in assetIds.Split(new[] { ',', ';', '，', '；' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (sb.Length > 0)
                sb.Append(", ");
            sb.Append(Describe(id) ?? id);
        }
        return sb.ToString();
    }

    private static string Build(string id, string label, string endpoint)
    {
        var name = string.IsNullOrWhiteSpace(label) || string.Equals(label, id, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : label;
        if (name.Length > 60)
            name = name[..60] + "…";

        var text = new StringBuilder();
        if (name.Length > 0)
            text.Append(name);
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            if (text.Length > 0)
                text.Append(' ');
            text.Append('(').Append(endpoint).Append(')');
        }
        if (text.Length > 0)
            text.Append(' ');
        text.Append('[').Append(id).Append(']');
        return text.ToString();
    }
}
