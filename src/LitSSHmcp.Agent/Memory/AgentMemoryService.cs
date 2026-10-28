using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>长期记忆服务：向量化并索引文本，按查询召回相关记忆片段；支持结构化核心记忆（kind/key/频次/更新）。</summary>
public sealed class AgentMemoryService
{
    /// <summary>结构化记忆允许的类别（模型写入时校验；raw 由系统自动管理）。</summary>
    public static readonly IReadOnlyList<string> Kinds = new[] { "task", "script", "asset" };

    /// <summary>事件记录允许的状态。</summary>
    public static readonly IReadOnlyList<string> IncidentStatuses = new[] { "open", "resolved", "superseded" };

    /// <summary>默认召回相似度阈值（agent.memory.recallThreshold 未配置时）。</summary>
    public const double DefaultRecallThreshold = 0.15;

    /// <summary>默认相对阈值比例：有效阈值 = max(RecallThreshold, 本类 top1 * 0.8)。</summary>
    public const double DefaultRecallRelativeMargin = 0.8;

    /// <summary>近重复判定阈值：新内容与既有任意条目的余弦高于该值时合并（不再新增）。</summary>
    public const double NearDuplicateThreshold = 0.92;

    private static readonly Regex RootCauseSection = new(
        @"(?:根因|原因|root\s*cause)\s*[:：]\s*([^;；\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ActionSection = new(
        @"(?:处置|解决|修复|动作|actions?)\s*[:：]\s*([^;；\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>结构化内容最小长度（低于该值视为无信息量，拒绝写入）。</summary>
    public const int MinContentLength = 8;

    private readonly IAgentMemoryStore _store;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly int _topK;
    private readonly double _recallThreshold;
    private readonly double _recallRelativeMargin;
    private readonly string _embeddingModel;
    private readonly double _recencyHalfLifeDays;
    private readonly int _maxRecallChars;
    private readonly TimeSpan _embeddingTimeout;
    private readonly MemoryReranker? _reranker;

    private const int QueryCacheLimit = 64;
    private const int DefaultScanRows = 5000;
    private readonly Dictionary<string, float[]> _queryCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _queryCacheOrder = new();
    private readonly object _queryCacheLock = new();

    public AgentMemoryService(IAgentMemoryStore store, IEmbeddingGenerator<string, Embedding<float>> embedder, int topK,
        double recallThreshold = DefaultRecallThreshold, string? embeddingModel = null, double recencyHalfLifeDays = 30,
        int maxRecallChars = 1500, double embeddingTimeoutSeconds = 0, IChatClient? reranker = null,
        double recallRelativeMargin = DefaultRecallRelativeMargin)
    {
        _store = store;
        _embedder = embedder;
        _topK = Math.Max(1, topK);
        _recallThreshold = recallThreshold <= 0 || recallThreshold > 1 ? DefaultRecallThreshold : recallThreshold;
        _recallRelativeMargin = recallRelativeMargin > 0 && recallRelativeMargin <= 1
            ? recallRelativeMargin
            : 0;
        _embeddingModel = embeddingModel?.Trim() ?? string.Empty;
        _recencyHalfLifeDays = recencyHalfLifeDays > 0 ? recencyHalfLifeDays : 30;
        _maxRecallChars = Math.Max(200, maxRecallChars > 0 ? maxRecallChars : 1500);
        _embeddingTimeout = embeddingTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(embeddingTimeoutSeconds)
            : TimeSpan.Zero;
        _reranker = reranker is null ? null : new MemoryReranker(reranker);
    }

    /// <summary>当前 embeddings 模型标识；模型变化时文档索引会自动重建，避免旧维度向量不可用。</summary>
    public string EmbeddingModel => _embeddingModel;

    /// <summary>底层存储（供后台 consolidation/维护任务复用同一作用域连接）。</summary>
    public IAgentMemoryStore Store => _store;

    /// <summary>按 AgentConfig 创建记忆服务（供 Web/桌面审核等非运行时场景使用）；未启用/未配置模型返回 null。</summary>
    public static async Task<AgentMemoryService?> CreateFromConfigAsync(AgentConfig config, string? fallbackApiKey,
        MemoryAccessScope? access = null, CancellationToken ct = default)
    {
        var memoryConfig = config.Memory;
        if (memoryConfig?.Enabled != true || string.IsNullOrWhiteSpace(memoryConfig.Model))
            return null;
        var embedder = EmbeddingClientFactory.Create(memoryConfig, fallbackApiKey);
        var service = new AgentMemoryService(new AgentMemoryStore(null, access), embedder, memoryConfig.TopK,
            memoryConfig.RecallThreshold, memoryConfig.Model, memoryConfig.RecencyHalfLifeDays,
            memoryConfig.MaxRecallChars, memoryConfig.EmbeddingTimeoutSeconds,
            recallRelativeMargin: memoryConfig.RecallRelativeMargin);
        await service.InitializeAsync(ct);
        return service;
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

    /// <summary>按资产精确查询结构化记忆（走 memory_asset 关系）。</summary>
    public Task<List<MemoryRecord>> ListByAssetAsync(string assetId, string? kind, int limit,
        CancellationToken ct = default) => _store.ListByAssetAsync(assetId, kind, limit, ct);

    /// <summary>文档在记忆库中的稳定键（含作用域；同一文件不同用户互不覆盖）。</summary>
    public string CreateDocumentKey(string fullPath) => _store.CreateDocumentKey(fullPath);

    /// <summary>文档内容 hash + 当前 embeddings 模型未变时返回 true。</summary>
    public Task<bool> IsDocumentCurrentAsync(string docKey, string hash, CancellationToken ct = default) =>
        _store.IsDocumentCurrentAsync(docKey, hash, _embeddingModel, ct);

    /// <summary>批量向量化并原子替换一份文档的所有 chunk；旧版整文件索引一并清理。</summary>
    public async Task IndexDocumentAsync(string docKey, string root, string legacySource,
        IReadOnlyList<string> sources, IReadOnlyList<string> contents, string hash, CancellationToken ct = default)
    {
        if (contents.Count == 0)
            return;
        if (sources.Count != contents.Count)
            throw new ArgumentException("文档分块与来源数量不一致");

        var embeddings = await _embedder.GenerateAsync(contents, cancellationToken: ct);
        var vectors = embeddings.Select(e => e.Vector.ToArray()).ToList();
        await _store.ReplaceDocumentChunksAsync(docKey, root, legacySource, hash, _embeddingModel,
            sources, contents, vectors, ct);
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
                if (IsPossibleConflict(near, content, assets))
                {
                    await _store.AddStructuredAsync(kind, key, $"{kind}:{key}", content, vector,
                        string.Join(",", assets), string.Join(",", tagList), 1, ct);
                    return $"已新增记忆 [{kind}:{key}] (与 [{near.Kind}:{near.Key}] 可能冲突, 未自动合并; " +
                           "请核对后 update status=superseded 或改用更具体的 key)";
                }

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

    private static bool IsPossibleConflict(MemoryRecord near, string content, IReadOnlyCollection<string> newAssets)
    {
        if (newAssets.Count == 0)
            return false;
        var nearAssets = SplitList(near.AssetIds);
        if (nearAssets.Count == 0 || !nearAssets.Overlaps(newAssets))
            return false;

        var nearCause = ExtractSection(RootCauseSection, near.Content);
        var newCause = ExtractSection(RootCauseSection, content);
        if (nearCause is not null && newCause is not null &&
            !string.Equals(nearCause, newCause, StringComparison.OrdinalIgnoreCase))
            return true;

        var nearAction = ExtractSection(ActionSection, near.Content);
        var newAction = ExtractSection(ActionSection, content);
        return nearAction is not null && newAction is not null &&
               !string.Equals(nearAction, newAction, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractSection(Regex pattern, string text)
    {
        var match = pattern.Match(text);
        if (!match.Success)
            return null;
        var value = Regex.Replace(match.Groups[1].Value.Trim(), @"\s+", " ");
        return value.TrimEnd('。', '.', ';', '；', ',', '，');
    }

    /// <summary>在同 kind 的既有条目中找与给定向量余弦最高的超阈值条目（用于近重复合并）。</summary>
    private async Task<MemoryRecord?> FindNearDuplicateAsync(string kind, string content, float[] vector, CancellationToken ct)
    {
        // 候选：同 kind、可写范围内的活跃条目（按更新时间取前 50，避免全表扫）；
        // 隔离模式下普通成员不会把私有经验合并进只读的团队记忆
        var candidates = await _store.ListAsync(kind, null, 50, false, ct, writableOnly: true);
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

    /// <summary>列出一条记忆的历史版本（每次更新/删除/归档前归档，供审计与回滚）。</summary>
    public async Task<string> HistoryAsync(string kind, string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(key))
            return "错误: kind 与 key 为必填";
        kind = kind.Trim();
        key = key.Trim();

        var existing = await _store.FindByKeyAsync(kind, key, ct);
        if (existing is null)
            return $"(未找到记忆 [{kind}:{key}], 无法查看历史)";

        var versions = await _store.ListVersionsAsync(existing.Id, ct);
        if (versions.Count == 0)
            return $"(记忆 [{kind}:{key}] 暂无历史版本)";

        var sb = new StringBuilder();
        sb.AppendLine($"记忆 [{kind}:{key}] 的历史版本 (当前使用 {existing.Freq} 次, 共 {versions.Count} 个快照):");
        foreach (var v in versions)
        {
            sb.Append("- v").Append(v.Version)
                .Append(" 状态=").Append(v.Status)
                .Append(" 使用=").Append(v.Freq)
                .Append(" 归档=").Append(ShortDate(v.ArchivedAt));
            if (!string.IsNullOrEmpty(v.AssetIds)) sb.Append(" 资产=").Append(v.AssetIds);
            sb.AppendLine();
            sb.Append("  ").AppendLine(Truncate(v.Content, 300));
        }
        return sb.ToString().Trim();
    }

    /// <summary>把记忆回滚到指定历史版本（当前状态会先归档，回滚本身可撤销）。</summary>
    public async Task<string> RollbackAsync(string kind, string key, int version, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(key))
            return "错误: kind 与 key 为必填";
        if (version <= 0)
            return "错误: version 必须为正整数";
        kind = kind.Trim();
        key = key.Trim();

        var existing = await _store.FindByKeyAsync(kind, key, ct);
        if (existing is null)
            return $"(未找到记忆 [{kind}:{key}], 无法回滚)";
        if (!await _store.RestoreVersionAsync(existing.Id, version, ct))
            return $"错误: 记忆 [{kind}:{key}] 没有 v{version} 的历史版本, 或当前身份无权回滚团队记忆";

        return $"已回滚记忆 [{kind}:{key}] 到 v{version}; 回滚前状态已归档, 可继续用历史/回滚撤销。";
    }

    /// <summary>归档一条记忆（状态 superseded，不再被召回，但保留历史可回滚）。</summary>
    public async Task<string> ArchiveAsync(string kind, string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(key))
            return "错误: kind 与 key 为必填";
        kind = kind.Trim();
        key = key.Trim();

        var existing = await _store.FindByKeyAsync(kind, key, ct);
        if (existing is null)
            return $"(未找到记忆 [{kind}:{key}], 未归档)";
        if (!await _store.ArchiveByIdAsync(existing.Id, ct))
            return $"错误: 无法归档 [{kind}:{key}] (无权限或条目已变化)";

        return $"已归档记忆 [{kind}:{key}] (状态 superseded, 召回不再出现; 可查看历史并回滚恢复)";
    }

    /// <summary>删除一条结构化记忆。</summary>
    public async Task<string> DeleteAsync(string kind, string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(key))
            return "错误: kind 与 key 为必填";
        var ok = await _store.DeleteAsync(kind.Trim(), key.Trim(), ct);
        return ok ? $"已删除记忆 [{kind.Trim()}:{key.Trim()}]" : $"(未找到记忆 [{kind.Trim()}:{key.Trim()}], 未删除)";
    }

    /// <summary>记录一次事件/事故（现象→证据→根因→处置→结果），并写入资产关系。</summary>
    public async Task<string> SaveIncidentAsync(string title, string symptom, string assetIds, string rootCause,
        string actions, string outcome, string evidence, double confidence, string? status,
        CancellationToken ct = default)
    {
        title = (title ?? string.Empty).Trim();
        if (title.Length == 0)
            return "错误: title 为必填";
        if (title.Length > 200)
            return "错误: title 过长(≤200 字符)";

        symptom = (symptom ?? string.Empty).Trim();
        rootCause = (rootCause ?? string.Empty).Trim();
        actions = (actions ?? string.Empty).Trim();
        outcome = (outcome ?? string.Empty).Trim();
        evidence = (evidence ?? string.Empty).Trim();
        if (symptom.Length > 8000 || rootCause.Length > 8000 || actions.Length > 8000 ||
            outcome.Length > 8000 || evidence.Length > 8000)
            return "错误: 事件字段过长(单字段≤8000 字符)";

        var combined = string.Join("\n", title, symptom, rootCause, actions, outcome, evidence);
        if (MemoryQuality.ContainsSensitive(combined))
            return "错误: 事件内容疑似包含敏感信息(密码/密钥/令牌等); 请引用凭据别名或证据路径";

        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? "resolved" : status.Trim().ToLowerInvariant();
        if (normalizedStatus is not ("open" or "resolved"))
            return "错误: status 只能是 open(进行中) 或 resolved(已解决); 过时事件请用 supersede";

        var normalizedAssets = string.Join(",", NormalizeList(assetIds));
        if (!double.IsFinite(confidence))
            confidence = 0.5;
        var sessionId = LocalAudit.AmbientSessionId ?? string.Empty;
        var id = await _store.AddIncidentAsync(title, symptom, normalizedAssets, rootCause, actions,
            outcome, evidence, Math.Clamp(confidence, 0, 1), normalizedStatus, sessionId, ct);
        return $"已记录事件 #{id} [{normalizedStatus}]: {title}" +
               (normalizedAssets.Length > 0 ? $" (资产 {normalizedAssets})" : string.Empty);
    }

    /// <summary>列出/查看事件记录。</summary>
    public async Task<string> ListIncidentsAsync(string? assetId, string? status, int limit,
        CancellationToken ct = default)
    {
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        if (normalizedStatus is not null && !IncidentStatuses.Contains(normalizedStatus))
            return $"错误: status 仅支持 {string.Join("/", IncidentStatuses)}";

        var records = await _store.ListIncidentsAsync(assetId?.Trim(), normalizedStatus,
            limit <= 0 ? 20 : limit, ct);
        if (records.Count == 0)
            return "(暂无匹配的事件记录)";

        var sb = new StringBuilder();
        foreach (var r in records)
        {
            sb.Append("- #").Append(r.Id).Append(" [").Append(r.Status).Append("] ").Append(r.Title);
            if (!string.IsNullOrWhiteSpace(r.AssetIds)) sb.Append(" | 资产=").Append(r.AssetIds);
            sb.Append(" | 置信度=").Append(r.Confidence.ToString("0.##", CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(r.UpdatedAt))
                sb.Append(" | 更新=").Append(r.UpdatedAt[..Math.Min(10, r.UpdatedAt.Length)]);
            if (r.SupersededBy is long next) sb.Append(" | 已被 #").Append(next).Append(" 取代");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(r.Symptom)) sb.Append("  现象: ").AppendLine(Truncate(r.Symptom, 300));
            if (!string.IsNullOrWhiteSpace(r.RootCause)) sb.Append("  根因: ").AppendLine(Truncate(r.RootCause, 300));
            if (!string.IsNullOrWhiteSpace(r.Actions)) sb.Append("  处置: ").AppendLine(Truncate(r.Actions, 300));
            if (!string.IsNullOrWhiteSpace(r.Outcome)) sb.Append("  结果: ").AppendLine(Truncate(r.Outcome, 300));
            if (!string.IsNullOrWhiteSpace(r.Evidence)) sb.Append("  证据: ").AppendLine(Truncate(r.Evidence, 300));
        }
        return sb.ToString().Trim();
    }

    /// <summary>把旧事件标记为 superseded 并指向新事件。</summary>
    public async Task<string> SupersedeIncidentAsync(long oldId, long newId, CancellationToken ct = default)
    {
        if (oldId <= 0 || newId <= 0)
            return "错误: oldId/newId 必须为正整数";
        if (oldId == newId)
            return "错误: 新旧事件不能相同";
        if (await _store.GetIncidentAsync(newId, ct) is null)
            return $"(未找到新事件 #{newId}, 未执行取代)";
        if (!await _store.SupersedeIncidentAsync(oldId, newId, ct))
            return $"错误: 事件 #{oldId} 不存在、已过时或当前身份无权修改";
        return $"已把事件 #{oldId} 标记为 superseded，指向 #{newId}";
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

        using var timeout = _embeddingTimeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (timeout is not null)
            timeout.CancelAfter(_embeddingTimeout);
        var token = timeout?.Token ?? ct;

        var vector = await EmbedQueryAsync(query, token);

        var rawOnly = string.Equals(kind, "raw", StringComparison.OrdinalIgnoreCase);
        var structuredOnly = !string.IsNullOrWhiteSpace(kind) && !rawOnly;
        // 结构化优先，raw 预留小配额；topK=1 时先给结构化，但 raw 仍取候选用于兜底。
        var rawReserve = rawOnly ? _topK : structuredOnly ? 0 : _topK <= 1 ? 0 : Math.Min(2, _topK - 1);
        var structuredQuota = rawOnly ? 0 : _topK - rawReserve;
        var rawFetch = rawOnly ? _topK : structuredOnly ? 0 : Math.Max(2, _topK);

        // 结构化 / raw 分开取候选：避免 raw 高分把结构化经验挤出 topK，也给 raw 固定小配额。
        var structuredTask = structuredQuota == 0
            ? Task.FromResult(new List<MemoryHit>())
            : _store.SearchAsync(vector, Math.Max(_topK, structuredQuota) * 2, kind, activeOnly: true, assetIds,
                includeRaw: false, maxScanRows: DefaultScanRows, ct: token, queryText: query);
        var rawTask = rawFetch == 0
            ? Task.FromResult(new List<MemoryHit>())
            : _store.SearchAsync(vector, rawFetch * 4, "raw", activeOnly: true, assetIds,
                includeRaw: true, maxScanRows: DefaultScanRows, ct: token, queryText: query);
        await Task.WhenAll(structuredTask, rawTask);

        var now = DateTimeOffset.UtcNow;
        var structuredCandidates = structuredTask.Result;
        var rawCandidates = rawTask.Result;
        // 结构化经验是模型筛选后的高信号条目，保留绝对下限；相对阈值主要用于压制 raw 噪声。
        var structuredFloor = _recallThreshold;
        var rawFloor = EffectiveThreshold(rawCandidates);
        var structured = structuredCandidates
            .Where(h => h.Score > structuredFloor)
            .OrderByDescending(h => Rank(h, now))
            .ToList();
        var raw = rawCandidates
            .Where(h => h.Score > rawFloor)
            .OrderByDescending(h => Rank(h, now))
            .ToList();

        var hits = new List<MemoryHit>(_topK);
        foreach (var hit in structured.Take(structuredQuota))
            hits.Add(hit);
        foreach (var hit in raw.Take(rawReserve))
            hits.Add(hit);
        // 某一类不足配额时，用另一类补齐，但总数不超过 topK。
        if (hits.Count < _topK)
        {
            foreach (var hit in structured.Skip(structuredQuota).Concat(raw.Skip(rawReserve)))
            {
                if (hits.Count >= _topK)
                    break;
                hits.Add(hit);
            }
        }
        if (hits.Count == 0)
            return null;
        if (_reranker is not null && hits.Count > 1)
            hits = await _reranker.RerankAsync(query, hits, ct);

        var sb = new StringBuilder();
        foreach (var hit in hits)
        {
            // 结构化记忆带语义键与频次展示，便于模型判断"这是可复用经验"；资产相关经验额外标注
            var tag = hit.Kind == "raw"
                ? RawSourceLabel(hit.Source)
                : $"{hit.Kind}:{hit.Key} ×{hit.Freq}";
            var age = hit.Kind == "raw" ? null : FormatAge(hit);
            if (age is not null)
                tag += $", {age}";
            if (hit.Kind != "raw" && !string.IsNullOrWhiteSpace(hit.AssetIds))
                tag += $" → {Truncate(hit.AssetIds, 120)}";   // 该经验验证过的资产
            var line = $"- [{tag}] {Truncate(hit.Content, 500)}";
            if (sb.Length + line.Length + 1 > _maxRecallChars)
            {
                var remaining = _maxRecallChars - sb.Length - 1;
                if (remaining >= 60)
                    sb.AppendLine(Truncate(line, remaining));
                break;
            }
            sb.AppendLine(line);
        }
        return sb.Length == 0 ? null : sb.ToString().Trim();
    }

    /// <summary>按类别的相对阈值：max(绝对下限, top1 * 相对比例)，避免跨模型固定 0.15 的漂移。</summary>
    private double EffectiveThreshold(IReadOnlyList<MemoryHit> candidates)
    {
        if (_recallRelativeMargin <= 0 || candidates.Count == 0)
            return _recallThreshold;
        var top = candidates.Max(h => h.Score);
        return Math.Max(_recallThreshold, top * _recallRelativeMargin);
    }

    private static string? FormatAge(MemoryHit hit)
    {
        var value = string.IsNullOrWhiteSpace(hit.UpdatedAt) ? hit.CreatedAt : hit.UpdatedAt;
        if (string.IsNullOrWhiteSpace(value) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                out var timestamp))
            return null;
        var days = (DateTimeOffset.UtcNow - timestamp).TotalDays;
        if (days < 30)
            return null;
        if (days < 365)
            return $"{days / 30:0}mo";
        return $"{days / 365:0}y";
    }

    /// <summary>时间衰减 + 频次微调后的排序分；分数差很大时仍以语义相关度为主。</summary>
    private double Rank(MemoryHit hit, DateTimeOffset now)
    {
        var timestamp = ParseTimestamp(hit.Kind == "raw"
            ? hit.CreatedAt
            : string.IsNullOrWhiteSpace(hit.UpdatedAt) ? hit.CreatedAt : hit.UpdatedAt);
        var ageDays = Math.Max(0, (now - timestamp).TotalDays);
        var recency = Math.Pow(0.5, ageDays / _recencyHalfLifeDays);
        var freqBoost = 1 + 0.02 * Math.Min(Math.Max(hit.Freq - 1, 0), 20);
        return hit.Score * (0.85 + 0.15 * recency) * freqBoost;
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

    /// <summary>查询向量缓存：相同问题（如连续追问）不重复调用远程 embeddings。</summary>
    private async Task<float[]> EmbedQueryAsync(string query, CancellationToken ct)
    {
        var key = query.Trim();
        lock (_queryCacheLock)
        {
            if (_queryCache.TryGetValue(key, out var cached))
                return cached;
        }

        var vector = await EmbedAsync(key, ct);
        lock (_queryCacheLock)
        {
            if (!_queryCache.ContainsKey(key))
            {
                _queryCache[key] = vector;
                _queryCacheOrder.Enqueue(key);
                while (_queryCacheOrder.Count > QueryCacheLimit)
                    _queryCache.Remove(_queryCacheOrder.Dequeue());
            }
        }
        return vector;
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

    /// <summary>raw 记忆按会话分桶后，注入提示时仍显示为“会话”，避免把内部会话 ID 带进上下文。</summary>
    private static string RawSourceLabel(string source) =>
        source.StartsWith("会话:", StringComparison.Ordinal) ? "会话" : source;

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string ShortDate(string value) =>
        string.IsNullOrEmpty(value) ? "-" : value[..Math.Min(19, value.Length)];
}
