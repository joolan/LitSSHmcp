using System.Collections.ObjectModel;
using System.Text.Json;
using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public partial class AgentSettingsViewModel
{
    // ===== 记忆候选审核（后台 consolidation：批准写入事件 / 拒绝） =====

    public ObservableCollection<ConsolidationCandidateEntry> ConsolidationCandidates { get; } = new();

    public IReadOnlyList<string> ConsolidationStatusFilters { get; } =
        new[] { "待审核", "全部", "已应用", "已拒绝", "冲突" };

    private string _consolidationStatusFilter = "待审核";
    public string ConsolidationStatusFilter
    {
        get => _consolidationStatusFilter;
        set
        {
            if (_consolidationStatusFilter == value) return;
            _consolidationStatusFilter = value;
            OnPropertyChanged();
            _ = LoadConsolidationCandidatesAsync();
        }
    }

    private ConsolidationCandidateEntry? _selectedConsolidationCandidate;
    public ConsolidationCandidateEntry? SelectedConsolidationCandidate
    {
        get => _selectedConsolidationCandidate;
        set => Set(ref _selectedConsolidationCandidate, value);
    }

    public async Task LoadConsolidationCandidatesAsync()
    {
        try
        {
            var store = new AgentMemoryStore();
            var status = ConsolidationStatusFilter switch
            {
                "待审核" => "pending",
                "已应用" => "applied",
                "已拒绝" => "rejected",
                "冲突" => "conflict",
                _ => null
            };
            var records = await store.ListConsolidationCandidatesAsync(status, 200);
            ConsolidationCandidates.Clear();
            foreach (var record in records)
                ConsolidationCandidates.Add(ConsolidationCandidateEntry.From(record));
            StatusMessage = ConsolidationCandidates.Count == 0
                ? "暂无匹配的 consolidation 候选"
                : $"已加载 {ConsolidationCandidates.Count} 个候选";
        }
        catch (Exception ex)
        {
            StatusMessage = "读取 consolidation 候选失败: " + ex.Message;
        }
    }

    public async Task ApproveConsolidationCandidateAsync(ConsolidationCandidateEntry? entry)
    {
        if (entry is null)
            return;
        try
        {
            var memory = await TryCreateConsolidationMemoryAsync();
            var review = new ConsolidationReviewService(new AgentMemoryStore(), memory);
            var (ok, message, incidentId) = await review.ApproveAsync(entry.Id);
            StatusMessage = message + (incidentId is null ? string.Empty : $"（事件 #{incidentId}）");
            await LocalAudit.LogAsync("agent_consolidation", $"CONSOLIDATION_APPROVE #{entry.Id}",
                CommandStatus.Executed, result: StatusMessage);
            if (ok)
                await LoadConsolidationCandidatesAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = "批准候选失败: " + ex.Message;
        }
    }

    private async Task<AgentMemoryService?> TryCreateConsolidationMemoryAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var providers = AgentProviderGroupConfig.Flatten(config.Agent.Providers);
            var provider = providers.FirstOrDefault(p => p.Id == config.Agent.ActiveProviderId)
                           ?? providers.FirstOrDefault();
            return await AgentMemoryService.CreateFromConfigAsync(config.Agent, provider?.ApiKey);
        }
        catch
        {
            return null;
        }
    }

    public async Task RejectConsolidationCandidateAsync(ConsolidationCandidateEntry? entry, string? reason)
    {
        if (entry is null)
            return;
        try
        {
            var review = new ConsolidationReviewService(new AgentMemoryStore());
            var (ok, message) = await review.RejectAsync(entry.Id, reason);
            StatusMessage = message;
            await LocalAudit.LogAsync("agent_consolidation", $"CONSOLIDATION_REJECT #{entry.Id}",
                CommandStatus.Executed, result: $"{message}; reason={reason}");
            if (ok)
                await LoadConsolidationCandidatesAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = "拒绝候选失败: " + ex.Message;
        }
    }
}

/// <summary>记忆候选列表行（展示用；payload 保留 JSON 供审批/悬停查看）。</summary>
public sealed class ConsolidationCandidateEntry
{
    public long Id { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string PayloadJson { get; init; } = string.Empty;
    public string AssetIds { get; init; } = string.Empty;
    public string Evidence { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public string Status { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
    public string AppliedRef { get; init; } = string.Empty;
    public string RejectReason { get; init; } = string.Empty;

    public string Title => $"[{Status}] {PayloadTitle(PayloadJson)}";
    public string Preview => string.IsNullOrWhiteSpace(Evidence) ? "证据: -" : $"证据: {Evidence}";
    public string Meta
    {
        get
        {
            var text = $"#{Id} · {Kind} · 置信度 {Confidence:0.00}";
            if (!string.IsNullOrWhiteSpace(CreatedAt))
                text += $" · 创建 {CreatedAt[..Math.Min(10, CreatedAt.Length)]}";
            if (!string.IsNullOrWhiteSpace(AssetIds))
                text += $" · 资产 {AssetIds}";
            if (!string.IsNullOrWhiteSpace(AppliedRef))
                text += $" · 已应用 {AppliedRef}";
            if (!string.IsNullOrWhiteSpace(RejectReason))
                text += $" · 拒绝原因 {RejectReason}";
            return text;
        }
    }

    public static ConsolidationCandidateEntry From(ConsolidationCandidate record) => new()
    {
        Id = record.Id,
        Kind = record.Kind,
        PayloadJson = record.PayloadJson,
        AssetIds = record.AssetIds,
        Evidence = record.Evidence,
        Confidence = record.Confidence,
        Status = record.Status,
        CreatedAt = record.CreatedAt,
        AppliedRef = record.AppliedRef,
        RejectReason = record.RejectReason
    };

    private static string PayloadTitle(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.TryGetProperty("title", out var title) &&
                title.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(title.GetString()))
                return title.GetString()!;
            var assetId = root.TryGetProperty("assetId", out var asset) &&
                          asset.ValueKind == JsonValueKind.String ? asset.GetString() : null;
            var key = root.TryGetProperty("key", out var k) &&
                      k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            if (!string.IsNullOrWhiteSpace(assetId) || !string.IsNullOrWhiteSpace(key))
                return string.Join(" · ", new[] { assetId, key }.Where(v => !string.IsNullOrWhiteSpace(v)));
            return "(无标题)";
        }
        catch
        {
            return "(payload 解析失败)";
        }
    }
}
