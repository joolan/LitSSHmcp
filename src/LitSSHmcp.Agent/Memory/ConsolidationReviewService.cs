using System.Text.Json;

namespace LitSSHmcp.Agent;

/// <summary>
/// consolidation 候选审核：批准写入事件、拒绝候选、事件 supersede。
/// 供 Web 管理 API / 后续桌面审核面板调用；不自动执行脚本或覆盖资产事实。
/// </summary>
public sealed class ConsolidationReviewService
{
    private readonly IAgentMemoryStore _store;
    private readonly AgentMemoryService? _memory;

    public ConsolidationReviewService(IAgentMemoryStore store, AgentMemoryService? memory = null)
    {
        _store = store;
        _memory = memory;
    }

    public async Task<(bool Ok, string Message, long? IncidentId)> ApproveAsync(long candidateId,
        CancellationToken ct = default)
    {
        var candidate = await _store.GetConsolidationCandidateAsync(candidateId, ct);
        if (candidate is null)
            return (false, "候选不存在", null);
        if (candidate.Status is not ("pending" or "conflict"))
            return (false, $"候选状态 {candidate.Status} 不可批准", null);
        var payload = ParsePayload(candidate.PayloadJson);
        if (payload is null)
            return (false, "候选 payload 无效", null);
        var evidence = string.Join(" | ", new[] { candidate.Evidence, payload.Evidence }
            .Where(v => !string.IsNullOrWhiteSpace(v)));

        if (string.Equals(candidate.Kind, "incident", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(payload.Title))
                return (false, "候选 payload 缺少 title", null);
            if (MemoryQuality.ContainsSensitive(string.Join("\n", payload.Title, payload.Symptom,
                    payload.RootCause, payload.Actions, payload.Outcome, evidence)))
                return (false, "候选内容疑似包含敏感信息，已拒绝批准", null);

            var status = payload.Status is "open" or "resolved" ? payload.Status : "open";
            var incidentId = await _store.AddIncidentAsync(payload.Title, payload.Symptom, candidate.AssetIds,
                payload.RootCause, payload.Actions, payload.Outcome, evidence, candidate.Confidence,
                status, string.Empty, ct);
            await _store.UpdateConsolidationCandidateAsync(candidateId, "applied",
                $"incident:{incidentId}", string.Empty, ct);
            return (true, $"已批准候选 #{candidateId}，写入事件 #{incidentId}", incidentId);
        }

        if (string.Equals(candidate.Kind, "asset_fact", StringComparison.OrdinalIgnoreCase))
        {
            if (_memory is null)
                return (false, "记忆/Embeddings 未配置，无法写入资产事实", null);
            var assetId = string.IsNullOrWhiteSpace(payload.AssetId) ? candidate.AssetIds : payload.AssetId;
            if (string.IsNullOrWhiteSpace(assetId) || string.IsNullOrWhiteSpace(payload.Key) ||
                string.IsNullOrWhiteSpace(payload.Value))
                return (false, "asset_fact 缺少 assetId/key/value", null);
            if (MemoryQuality.ContainsSensitive(assetId + "\n" + payload.Key + "\n" + payload.Value))
                return (false, "资产事实疑似包含敏感信息，已拒绝批准", null);

            var memoryKey = $"资产事实:{assetId}:{payload.Key}";
            var content = $"{assetId} 的 {payload.Key}: {payload.Value}" +
                          (string.IsNullOrWhiteSpace(evidence) ? string.Empty : $"\n证据: {evidence}");
            var saved = await _memory.SaveAsync("asset", memoryKey, content, assetId, "consolidation", ct);
            if (saved.StartsWith("错误:", StringComparison.Ordinal))
                return (false, saved, null);
            await _store.UpdateConsolidationCandidateAsync(candidateId, "applied",
                $"memory:{memoryKey}", string.Empty, ct);
            return (true, $"已批准候选 #{candidateId}，写入资产事实 {memoryKey}", null);
        }

        if (string.Equals(candidate.Kind, "script", StringComparison.OrdinalIgnoreCase))
        {
            if (_memory is null)
                return (false, "记忆/Embeddings 未配置，无法写入脚本经验", null);
            if (string.IsNullOrWhiteSpace(payload.Content))
                return (false, "script 候选缺少 content", null);
            var title = string.IsNullOrWhiteSpace(payload.Title) ? "可复用脚本" : payload.Title;
            var memoryKey = "脚本:" + title;
            var content = $"用途: {payload.Purpose}\n语言: {payload.Language}\n```{payload.Language}\n{payload.Content}\n```";
            var saved = await _memory.SaveAsync("script", memoryKey, content, candidate.AssetIds,
                "consolidation", ct);
            if (saved.StartsWith("错误:", StringComparison.Ordinal))
                return (false, saved, null);
            await _store.UpdateConsolidationCandidateAsync(candidateId, "applied",
                $"memory:{memoryKey}", string.Empty, ct);
            return (true, $"已批准候选 #{candidateId}，写入脚本经验 {memoryKey}（未执行）", null);
        }

        return (false, $"暂不支持 {candidate.Kind} 候选", null);
    }

    public async Task<(bool Ok, string Message)> RejectAsync(long candidateId, string? reason,
        CancellationToken ct = default)
    {
        var candidate = await _store.GetConsolidationCandidateAsync(candidateId, ct);
        if (candidate is null)
            return (false, "候选不存在");
        if (candidate.Status is not ("pending" or "conflict"))
            return (false, $"候选状态 {candidate.Status} 不可拒绝");

        var text = string.IsNullOrWhiteSpace(reason) ? "人工拒绝" : reason.Trim();
        if (text.Length > 500)
            text = text[..500];
        var ok = await _store.UpdateConsolidationCandidateAsync(candidateId, "rejected", string.Empty, text, ct);
        return ok ? (true, $"已拒绝候选 #{candidateId}") : (false, "更新候选失败");
    }

    public async Task<(bool Ok, string Message)> SupersedeIncidentAsync(long oldId, long newId,
        CancellationToken ct = default)
    {
        if (oldId <= 0 || newId <= 0 || oldId == newId)
            return (false, "oldId/newId 必须为正整数且不相同");
        if (await _store.GetIncidentAsync(newId, ct) is null)
            return (false, $"新事件 #{newId} 不存在");
        var ok = await _store.SupersedeIncidentAsync(oldId, newId, ct);
        return ok ? (true, $"事件 #{oldId} 已标记 superseded → #{newId}")
            : (false, $"事件 #{oldId} 不存在、已过时或无权修改");
    }

    private static CandidatePayload? ParsePayload(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            return new CandidatePayload(
                ReadString(root, "title") ?? string.Empty,
                ReadString(root, "symptom") ?? string.Empty,
                ReadString(root, "rootCause") ?? string.Empty,
                ReadString(root, "actions") ?? string.Empty,
                ReadString(root, "outcome") ?? string.Empty,
                ReadString(root, "evidence") ?? string.Empty,
                ReadString(root, "status") ?? string.Empty,
                ReadString(root, "assetId") ?? string.Empty,
                ReadString(root, "key") ?? string.Empty,
                ReadString(root, "value") ?? string.Empty,
                ReadString(root, "purpose") ?? string.Empty,
                ReadString(root, "language") ?? "bash",
                ReadString(root, "content") ?? string.Empty);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record CandidatePayload(string Title, string Symptom, string RootCause, string Actions,
        string Outcome, string Evidence, string Status, string AssetId, string Key, string Value,
        string Purpose, string Language, string Content);
}
