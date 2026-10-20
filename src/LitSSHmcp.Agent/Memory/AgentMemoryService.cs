using System.Text;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>长期记忆服务：向量化并索引文本，按查询召回相关记忆片段；支持结构化核心记忆（kind/key/频次/更新）。</summary>
public sealed class AgentMemoryService
{
    /// <summary>结构化记忆允许的类别（模型写入时校验；raw 由系统自动管理）。</summary>
    public static readonly IReadOnlyList<string> Kinds = new[] { "task", "script", "asset" };

    /// <summary>默认召回相似度阈值（agent.memory.recallThreshold 未配置时）。</summary>
    public const double DefaultRecallThreshold = 0.15;

    /// <summary>近重复判定阈值：新内容与既有任意条目的余弦高于该值时合并（不再新增）。</summary>
    public const double NearDuplicateThreshold = 0.92;

    /// <summary>结构化内容最小长度（低于该值视为无信息量，拒绝写入）。</summary>
    public const int MinContentLength = 8;

    private readonly IAgentMemoryStore _store;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly int _topK;
    private readonly double _recallThreshold;

    public AgentMemoryService(IAgentMemoryStore store, IEmbeddingGenerator<string, Embedding<float>> embedder, int topK,
        double recallThreshold = DefaultRecallThreshold)
    {
        _store = store;
        _embedder = embedder;
        _topK = Math.Max(1, topK);
        _recallThreshold = recallThreshold <= 0 || recallThreshold > 1 ? DefaultRecallThreshold : recallThreshold;
    }

    public Task InitializeAsync(CancellationToken ct = default) => _store.InitializeAsync(ct);
    public Task<int> CountAsync(CancellationToken ct = default) => _store.CountAsync(ct);

    /// <summary>索引一段文本（source 用于展示来源；相同内容不重复索引）。系统侧自动索引（会话/文档），kind=raw。</summary>
    public async Task IndexAsync(string source, string content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;
        if (await _store.ExistsAsync(source, content, ct))
            return;

        var vector = await EmbedAsync(content, ct);
        await _store.AddAsync(source, content, vector, ct);
    }

    /// <summary>同源(raw)保留条数上限：超出时删除最旧的，防止会话片段无限膨胀。</summary>
    public async Task<int> PruneRawAsync(string source, int keep, CancellationToken ct = default)
    {
        if (keep < 0)
            return 0;
        return await _store.PruneRawAsync(source, keep, ct);
    }

    /// <summary>
    /// 写入/合并一条结构化核心记忆：同 (kind,key) 已存在则覆盖为最新有效内容、频次+1、资产取并集（保持"更新"语义）。
    /// 质量门：敏感词拒绝、过短拒绝；与既有任意条目余弦 &gt; <see cref="NearDuplicateThreshold"/> 时合并而非新增。
    /// 返回给人看的结果说明；参数非法时返回以 "错误:" 开头的说明（不抛出，便于工具直接回传）。
    /// </summary>
    public async Task<string> SaveAsync(string kind, string key, string content, string? assetIds, string? tags, CancellationToken ct = default)
    {
        var error = Validate(kind, key, content);
        if (error is not null)
            return error;

        kind = kind!.Trim();
        key = key!.Trim();
        content = content!.Trim();
        if (content.Length < MinContentLength)
            return $"错误: content 过短(少于 {MinContentLength} 字符, 不构成可复用经验)";
        if (MemoryQuality.ContainsSensitive(content) || MemoryQuality.ContainsSensitive(key))
            return "错误: content/key 疑似包含敏感信息(密码/密钥/令牌等), 记忆不存储凭据; 请改为引用凭据别名后重试";

        var assets = NormalizeList(assetIds);
        var tagList = NormalizeList(tags);

        var existing = await _store.FindByKeyAsync(kind, key, ct);
        var vector = await EmbedAsync(content, ct);

        if (existing is null)
        {
            // 近重复合并：与既有任意结构化条目内容高度相似时，挂到既有条目上（频次+1）而非新增
            var near = await FindNearDuplicateAsync(kind, content, vector, ct);
            if (near is not null)
            {
                var mergedAssets = MergeAssets(near.AssetIds, assets);
                var mergedTags = MergeAssets(near.Tags, tagList);
                var status = near.Status == "superseded" ? "active" : near.Status;
                await _store.UpdateStructuredAsync(near.Id, content, vector, mergedAssets, mergedTags, status, 1, ct);
                return $"已合并近重复记忆 [{near.Kind}:{near.Key}] (原 key={key}; 频次 {near.Freq + 1})";
            }

            await _store.AddStructuredAsync(kind, key, $"{kind}:{key}", content, vector,
                string.Join(",", assets), string.Join(",", tagList), 1, ct);
            return $"已新增记忆 [{kind}:{key}] (频次 1, 资产 {assets.Count} 个)";
        }

        var mergedExact = MergeAssets(existing.AssetIds, assets);
        var mergedExactTags = MergeAssets(existing.Tags, tagList);
        var exactStatus = existing.Status == "superseded" ? "active" : existing.Status;   // 再次成功使用=重新激活
        await _store.UpdateStructuredAsync(existing.Id, content, vector, mergedExact, mergedExactTags, exactStatus, 1, ct);
        return $"已更新记忆 [{kind}:{key}] (频次 {existing.Freq + 1}, 资产 {SplitList(mergedExact).Count} 个)";
    }

    /// <summary>在同 kind 的既有条目中找与给定向量余弦最高的超阈值条目（用于近重复合并）。</summary>
    private async Task<MemoryRecord?> FindNearDuplicateAsync(string kind, string content, float[] vector, CancellationToken ct)
    {
        // 候选：同 kind、内容长度相近的活跃条目（按更新时间取前 50，避免全表扫）
        var candidates = await _store.ListAsync(kind, null, 50, false, ct);
        MemoryRecord? best = null;
        var bestScore = NearDuplicateThreshold;
        foreach (var c in candidates)
        {
            var existingVector = await _store.GetVectorAsync(c.Id, ct);
            if (existingVector is null || existingVector.Length != vector.Length)
                continue;
            var score = VectorMath.Cosine(vector, existingVector);
            if (score >= bestScore)
            {
                bestScore = score;
                best = c;
            }
        }
        return best;
    }

    /// <summary>修改已有结构化记忆的字段（不增频次）；content 变更会重新向量化。status=active|superseded。</summary>
    public async Task<string> UpdateAsync(string kind, string key, string? content, string? assetIds, string? tags,
        string? status, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(key))
            return "错误: kind 与 key 为必填";
        kind = kind.Trim();
        key = key.Trim();

        var existing = await _store.FindByKeyAsync(kind, key, ct);
        if (existing is null)
            return $"(未找到记忆 [{kind}:{key}, 未修改) 可用 ops_memory_list 查看现有条目";

        var newContent = string.IsNullOrWhiteSpace(content) ? existing.Content : content.Trim();
        var newAssets = assetIds is null ? existing.AssetIds : string.Join(",", NormalizeList(assetIds));
        var newTags = tags is null ? existing.Tags : string.Join(",", NormalizeList(tags));
        var newStatus = string.IsNullOrWhiteSpace(status) ? existing.Status : status.Trim().ToLowerInvariant();
        if (newStatus is not ("active" or "superseded"))
            return "错误: status 只能是 active(有效) 或 superseded(已过时)";

        var vector = await EmbedAsync(newContent, ct);
        await _store.UpdateStructuredAsync(existing.Id, newContent, vector, newAssets, newTags, newStatus, 0, ct);
        return $"已更新记忆 [{kind}:{key}] (状态 {newStatus}{(newStatus == "superseded" ? ", 之后召回将不再出现" : string.Empty)})";
    }

    /// <summary>列出结构化记忆（供模型查询同类经验/脚本台账）。</summary>
    public async Task<string> ListAsync(string? kind, string? assetId, int limit, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(kind) && !Kinds.Contains(kind.Trim(), StringComparer.OrdinalIgnoreCase))
            return $"错误: kind 仅支持 {string.Join("/", Kinds)}";
        var records = await _store.ListAsync(kind?.Trim(), assetId?.Trim(), limit <= 0 ? 50 : limit, ct: ct);
        if (records.Count == 0)
            return "(暂无结构化记忆)";

        var sb = new StringBuilder();
        foreach (var r in records)
        {
            sb.Append("- [").Append(r.Kind).Append(':').Append(r.Key).Append("] 频次=").Append(r.Freq);
            if (!string.IsNullOrEmpty(r.AssetIds)) sb.Append(" 资产=").Append(r.AssetIds);
            if (!string.IsNullOrEmpty(r.Tags)) sb.Append(" 标签=").Append(r.Tags);
            if (r.Status != "active") sb.Append(" 状态=").Append(r.Status);
            if (!string.IsNullOrEmpty(r.UpdatedAt)) sb.Append(" 更新=").Append(r.UpdatedAt[..Math.Min(10, r.UpdatedAt.Length)]);
            sb.AppendLine();
            sb.Append("  ").AppendLine(Truncate(r.Content, 400));
        }
        return sb.ToString().Trim();
    }

    /// <summary>删除一条结构化记忆。</summary>
    public async Task<string> DeleteAsync(string kind, string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(key))
            return "错误: kind 与 key 为必填";
        var ok = await _store.DeleteAsync(kind.Trim(), key.Trim(), ct);
        return ok ? $"已删除记忆 [{kind.Trim()}:{key.Trim()}]" : $"(未找到记忆 [{kind.Trim()}:{key.Trim()}], 未删除)";
    }

    /// <summary>
    /// 按查询召回 topK 记忆，拼成可直接注入提示的文本；无相关记忆返回 null。kind 非空时只在该类别内检索。
    /// assetIds 非空时优先保留关联资产有交集的条目（raw 与通用经验仍可召回），减少跨资产误用。
    /// 结构化经验优先；raw(会话/文档片段) 最多占 min(2, topK) 席, 避免挤占复用价值更高的结构化条目。
    /// </summary>
    public async Task<string?> RecallAsync(string query, string? kind = null,
        IReadOnlyCollection<string>? assetIds = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var vector = await EmbedAsync(query, ct);
        // 多取一些候选再重排：结构化优先、raw 限量；资产过滤在存储层完成
        var candidates = (await _store.SearchAsync(vector, _topK * 4, kind, activeOnly: true, assetIds, ct))
            .Where(h => h.Score > _recallThreshold)
            .OrderByDescending(h => h.Kind != "raw")   // 结构化在前
            .ThenByDescending(h => h.Score)
            .ThenByDescending(h => h.Freq)
            .ToList();

        var maxRaw = Math.Min(2, _topK);
        var rawUsed = 0;
        var hits = new List<MemoryHit>();
        foreach (var hit in candidates)
        {
            if (hits.Count >= _topK)
                break;
            if (hit.Kind == "raw" && ++rawUsed > maxRaw)
                continue;
            hits.Add(hit);
        }
        if (hits.Count == 0)
            return null;

        var sb = new StringBuilder();
        foreach (var hit in hits)
        {
            // 结构化记忆带语义键与频次展示，便于模型判断"这是可复用经验"；资产相关经验额外标注
            var tag = hit.Kind == "raw"
                ? hit.Source
                : $"{hit.Kind}:{hit.Key} ×{hit.Freq}";
            if (hit.Kind != "raw" && !string.IsNullOrWhiteSpace(hit.AssetIds))
                tag += $" → {Truncate(hit.AssetIds, 120)}";   // 该经验验证过的资产
            sb.AppendLine($"- [{tag}] {Truncate(hit.Content, 500)}");
        }
        return sb.ToString().Trim();
    }

    private static string? Validate(string? kind, string? key, string? content)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return "错误: kind 为必填";
        if (!Kinds.Contains(kind.Trim(), StringComparer.OrdinalIgnoreCase))
            return $"错误: kind 仅支持 {string.Join("/", Kinds)}";
        if (string.IsNullOrWhiteSpace(key))
            return "错误: key 为必填(语义唯一键, 如 磁盘满:ssh:web01)";
        if (string.IsNullOrWhiteSpace(content))
            return "错误: content 为必填";
        if (key.Trim().Length > 200)
            return "错误: key 过长(≤200 字符)";
        if (content.Trim().Length > 8000)
            return "错误: content 过长(≤8000 字符)";
        return null;
    }

    private static List<string> NormalizeList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new List<string>()
            : value.Split(new[] { ',', ';', '，' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .Where(v => v.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static string MergeAssets(string existing, List<string> added)
    {
        var set = SplitList(existing);
        foreach (var item in added)
            set.Add(item);
        return string.Join(",", set);
    }

    private static HashSet<string> SplitList(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()),
                StringComparer.OrdinalIgnoreCase);

    private async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var result = await _embedder.GenerateAsync(new[] { text }, cancellationToken: ct);
        return result[0].Vector.ToArray();
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
