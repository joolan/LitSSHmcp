using System.Text;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Agent;

/// <summary>
/// 资产画像（UI/工具共用）：配置关系 1 跳邻域 + 反向影响面 + 该资产及邻域的事实/历史事件/脚本。
/// 图谱部分只读人工配置关系（发现边以 topology/ops_insight(action=asset_profile) 为准）；UI 面板用它做轻量浏览。
/// </summary>
public static class AssetProfileService
{
    public static async Task<string> BuildAsync(IAgentMemoryStore? store, IConfigService? configService,
        string assetId, int limit = 10, CancellationToken ct = default)
    {
        assetId = (assetId ?? string.Empty).Trim();
        if (assetId.Length == 0)
            return "错误: assetId 为必填";
        limit = Math.Clamp(limit, 1, 50);

        var sb = new StringBuilder();
        sb.AppendLine($"## 资产画像 {assetId}");
        var related = new List<string>();
        if (configService is not null)
        {
            try
            {
                var config = await configService.LoadConfigAsync();
                AppendConfigGraph(sb, config, assetId, related);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"### 图谱\n(读取配置失败: {ex.Message})");
            }
        }

        if (store is not null)
        {
            var ids = new[] { assetId }
                .Concat(related)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();
            await AppendMemoryAsync(sb, store, ids, limit, ct);
        }

        return sb.ToString().Trim();
    }

    private static void AppendConfigGraph(StringBuilder sb, AppConfig config, string assetId, List<string> related)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in config.Servers ?? Array.Empty<SshServerConfig>())
            labels[AssetNode.Ssh(s.Id)] = s.Name;
        foreach (var d in config.DataSources ?? Array.Empty<DataSourceConfig>())
            labels[AssetNode.Ds(d.Id)] = $"{d.Type} {d.Name}";
        foreach (var a in config.Applications ?? Array.Empty<ApplicationConfig>())
            labels[AssetNode.App(a.Id)] = $"{a.Type} {a.Name}";
        var relations = (config.Relations ?? Array.Empty<RelationConfig>())
            .Where(r => !string.IsNullOrWhiteSpace(r.From) && !string.IsNullOrWhiteSpace(r.To))
            .ToList();

        sb.AppendLine("### 图谱（配置关系，1 跳）");
        if (!labels.ContainsKey(assetId) && !relations.Any(r => Same(r.From, assetId) || Same(r.To, assetId)))
        {
            sb.AppendLine("(配置中未找到该资产或关系；可尝试 topology_query 查询发现边)");
            return;
        }

        var oneHop = relations.Where(r => Same(r.From, assetId) || Same(r.To, assetId)).Take(30).ToList();
        foreach (var rel in oneHop)
        {
            sb.AppendLine($"- {Label(labels, rel.From)} -{rel.Type}-> {Label(labels, rel.To)}");
            var other = Same(rel.From, assetId) ? rel.To : rel.From;
            if (!Same(other, assetId) && !related.Contains(other, StringComparer.OrdinalIgnoreCase))
                related.Add(other);
        }
        if (oneHop.Count == 0)
            sb.AppendLine("(该资产暂无配置关系)");

        sb.AppendLine("### 影响面（反向依赖，最多 3 跳）");
        var impact = new List<(string Id, int Hops)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { assetId };
        var queue = new Queue<(string Id, int Hops)>();
        queue.Enqueue((assetId, 0));
        while (queue.Count > 0)
        {
            var (current, hops) = queue.Dequeue();
            if (hops >= 3) continue;
            foreach (var rel in relations.Where(r => Same(r.To, current)).Take(20))
            {
                if (!seen.Add(rel.From)) continue;
                impact.Add((rel.From, hops + 1));
                queue.Enqueue((rel.From, hops + 1));
                if (impact.Count >= 30) break;
            }
            if (impact.Count >= 30) break;
        }
        if (impact.Count == 0)
        {
            sb.AppendLine("(未发现反向依赖；可能没有下游应用/域名/端口)");
        }
        else
        {
            foreach (var (id, hops) in impact)
                sb.AppendLine($"- {Label(labels, id)} ({hops} 跳)");
        }
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

    private static string Label(IReadOnlyDictionary<string, string> labels, string id) =>
        labels.TryGetValue(id, out var label) && !string.IsNullOrWhiteSpace(label)
            ? $"{label} ({id})"
            : id;

    private static bool Same(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
