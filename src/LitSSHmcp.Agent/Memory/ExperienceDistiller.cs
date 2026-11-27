using System.Text;

namespace LitSSHmcp.Agent;

/// <summary>一次事件蒸馏的结果统计。</summary>
public sealed record ExperienceDistillReport(int Scanned, int Created, int Skipped);

/// <summary>
/// 把已解决的事件（agent_incident：现象/根因/处置/结果）自动蒸馏成**结构化经验草稿**。
///
/// 背景：库里 183 条记忆中 181 条是系统自动写的会话流水账，只有 2 条是模型主动沉淀的
/// 可复用经验——系统提示里"请写经验"的引导没转化成行为。这里不再依赖模型自觉，
/// 而是把已有的 59 条事件直接整理成草稿，模型只需用 ops_memory_write action=confirm_draft
/// 确认（可顺手改 key），不必从零撰写。
///
/// 草稿落在 agent_consolidation_candidate（status=pending），**不进召回**，因此不会污染上下文；
/// DedupeKey = "incident:&lt;id&gt;" 保证幂等，重启不会重复生成。不恢复任何后台审核 UI。
/// </summary>
public static class ExperienceDistiller
{
    public const int BatchSize = 100;

    public static async Task<ExperienceDistillReport> RunAsync(IAgentMemoryStore store,
        CancellationToken ct = default)
    {
        var incidents = await store.ListIncidentsAsync(null, "resolved", BatchSize, ct);
        var created = 0;
        var skipped = 0;
        foreach (var incident in incidents)
        {
            ct.ThrowIfCancellationRequested();
            var content = BuildContent(incident);
            if (content is null)
            {
                skipped++;
                continue;
            }
            var dedupeKey = $"incident:{incident.Id}";
            var id = await store.AddDraftAsync("task", BuildKey(incident), content, incident.AssetIds,
                dedupeKey, Math.Clamp(incident.Confidence, 0.3, 0.9), dedupeKey, ct);
            if (id is null) skipped++;
            else created++;
        }
        return new ExperienceDistillReport(incidents.Count, created, skipped);
    }

    /// <summary>至少要有「现象 + 根因/处置」两段才有复用价值，否则跳过。</summary>
    private static string? BuildContent(IncidentRecord incident)
    {
        var parts = new List<string>(4);
        Add(parts, "现象", incident.Symptom, 120);
        Add(parts, "根因", incident.RootCause, 160);
        Add(parts, "处置", incident.Actions, 200);
        Add(parts, "结果", incident.Outcome, 120);
        return parts.Count >= 2 ? string.Join("; ", parts) : null;
    }

    private static void Add(List<string> parts, string label, string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        var flat = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (flat.Length == 0)
            return;
        parts.Add($"{label}: {(flat.Length <= max ? flat : flat[..max] + "…")}");
    }

    /// <summary>默认 key 带事件 Id 保证唯一；模型确认时可用 key 参数改成语义化键。</summary>
    private static string BuildKey(IncidentRecord incident)
    {
        var seed = !string.IsNullOrWhiteSpace(incident.RootCause)
            ? incident.RootCause
            : !string.IsNullOrWhiteSpace(incident.Symptom) ? incident.Symptom : incident.Title;
        var flat = (seed ?? "经验").Replace('\n', ' ').Trim();
        if (flat.Length > 36)
            flat = flat[..36];
        var normalized = new StringBuilder(flat.Length + 12);
        foreach (var c in flat)
            normalized.Append(char.IsWhiteSpace(c) || c is ':' or ';' or ',' ? '-' : c);
        return $"{normalized.ToString().Trim('-')}:{incident.Id}";
    }
}
