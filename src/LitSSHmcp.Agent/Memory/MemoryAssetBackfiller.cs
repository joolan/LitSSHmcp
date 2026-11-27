namespace LitSSHmcp.Agent;

/// <summary>一次旧数据资产归属回填的结果统计。</summary>
public sealed record AssetBackfillReport(int Scanned, int Attributed, int Multi, int Unknown);

/// <summary>
/// 回填历史 raw 记忆的资产归属。今天的资产隔离只对**新写入**的 raw 生效，
/// 而库里 181 条 raw 有 172 条无归属，会被 <c>AssetIds=''</c> 放行给任意资产。
///
/// 两条线索：
/// 1) 会话维度：raw 的 Source 是「会话:&lt;id&gt;」，从 agent_messages 里该会话的
///    工具调用参数提取 serverId 并解析成资产 ID（工具参数比自然语言可靠）。
/// 2) 内容维度：扫描连续文本里出现的名称/主机/裸 ID/带前缀 ID。
///
/// 判定结果写回 AttributionState：
/// - attributed（1 个资产）：写入 AssetIds，之后严格按资产交集过滤；
/// - multi（≥2 个）：写入交集列表但仍降权，避免一轮扫多台时被任意一台独占；
/// - unknown（0 个）：不写 AssetIds，召回时按 <c>UnattributedScoreFactor</c> 降权。
///
/// 幂等：只处理 <c>AttributionState=''</c> 的 raw，重启不会重复执行。
/// </summary>
public static class MemoryAssetBackfiller
{
    /// <summary>单次最多处理的 raw 条数；超出留待下次启动继续。</summary>
    public const int BatchSize = 5000;

    public static async Task<AssetBackfillReport> RunAsync(IAgentMemoryStore store, string? assetGraph,
        CancellationToken ct = default)
    {
        var resolver = AssetIdResolver.FromPrompt(assetGraph);
        var hints = await store.ListSessionServerHintsAsync(ct);
        var bySession = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var hint in hints)
        {
            var asset = resolver.Resolve(hint.Reference);
            if (asset is null)
                continue;
            if (!bySession.TryGetValue(hint.SessionId, out var list))
                bySession[hint.SessionId] = list = new List<string>();
            if (!list.Contains(asset, StringComparer.OrdinalIgnoreCase))
                list.Add(asset);
        }

        var items = await store.ListRawForAttributionAsync(BatchSize, ct);
        var attributed = 0;
        var multi = 0;
        var unknown = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var found = new List<string>(AssetAttribution.MaxAssets);

            // 1) 会话工具参数（最可靠）优先；2) 再用内容文本补漏。
            var sid = ExtractSessionId(item.Source);
            if (sid is not null && bySession.TryGetValue(sid, out var sessionAssets))
                foreach (var asset in sessionAssets)
                    AddDistinct(found, asset);
            foreach (var asset in resolver.ResolveMentions(item.Content))
                AddDistinct(found, asset);

            if (found.Count == 1)
            {
                await store.SetRawAttributionAsync(item.Id, found[0], "attributed", ct);
                attributed++;
            }
            else if (found.Count > 1)
            {
                await store.SetRawAttributionAsync(item.Id, string.Join(",", found), "multi", ct);
                multi++;
            }
            else
            {
                await store.SetRawAttributionAsync(item.Id, string.Empty, "unknown", ct);
                unknown++;
            }
        }
        return new AssetBackfillReport(items.Count, attributed, multi, unknown);
    }

    private static void AddDistinct(List<string> found, string asset)
    {
        if (found.Count >= AssetAttribution.MaxAssets || string.IsNullOrWhiteSpace(asset))
            return;
        if (!found.Contains(asset, StringComparer.OrdinalIgnoreCase))
            found.Add(asset);
    }

    /// <summary>「会话:16」→「16」；「会话」/「文档:xxx」返回 null。</summary>
    private static string? ExtractSessionId(string source)
    {
        const string prefix = "会话:";
        if (!source.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var value = source[prefix.Length..].Trim();
        return value.Length == 0 ? null : value;
    }
}
