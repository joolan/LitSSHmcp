using System.Collections.ObjectModel;
using LitSSHmcp.Agent;
using LitSSHmcp.Agent.Sessions;

namespace LitSSHmcp.App.ViewModels;

public partial class AgentSettingsViewModel
{
    // ===== 运维视图：事件浏览 / 时间线 / 资产画像 =====

    public ObservableCollection<IncidentInsightEntry> OpsIncidents { get; } = new();

    public IReadOnlyList<string> OpsIncidentStatusFilters { get; } =
        new[] { "全部", "resolved", "open", "superseded" };

    private string _opsIncidentStatusFilter = "全部";
    public string OpsIncidentStatusFilter
    {
        get => _opsIncidentStatusFilter;
        set
        {
            if (_opsIncidentStatusFilter == value) return;
            _opsIncidentStatusFilter = value;
            OnPropertyChanged();
            _ = LoadOpsIncidentsAsync();
        }
    }

    private IncidentInsightEntry? _selectedOpsIncident;
    public IncidentInsightEntry? SelectedOpsIncident
    {
        get => _selectedOpsIncident;
        set => Set(ref _selectedOpsIncident, value);
    }

    private string _opsTimelineText = string.Empty;
    public string OpsTimelineText
    {
        get => _opsTimelineText;
        private set => Set(ref _opsTimelineText, value);
    }

    private string _opsAssetId = string.Empty;
    public string OpsAssetId
    {
        get => _opsAssetId;
        set => Set(ref _opsAssetId, value);
    }

    private string _opsAssetProfileText = string.Empty;
    public string OpsAssetProfileText
    {
        get => _opsAssetProfileText;
        private set => Set(ref _opsAssetProfileText, value);
    }

    public async Task LoadOpsIncidentsAsync()
    {
        try
        {
            var store = new AgentMemoryStore();
            var status = OpsIncidentStatusFilter is "全部" or "" ? null : OpsIncidentStatusFilter;
            var records = await store.ListIncidentsAsync(null, status, 200);
            OpsIncidents.Clear();
            foreach (var record in records)
                OpsIncidents.Add(IncidentInsightEntry.From(record));
            if (OpsIncidents.Count > 0)
            {
                SelectedOpsIncident = OpsIncidents[0];
                await LoadOpsTimelineAsync(SelectedOpsIncident);
            }
            else
            {
                SelectedOpsIncident = null;
                OpsTimelineText = "暂无事件。完成一次诊断/变更后会自动记录。";
            }
            StatusMessage = $"已加载 {OpsIncidents.Count} 个事件";
        }
        catch (Exception ex)
        {
            StatusMessage = "读取事件失败: " + ex.Message;
        }
    }

    public async Task LoadOpsTimelineAsync(IncidentInsightEntry? entry)
    {
        if (entry is null)
            return;
        try
        {
            var tool = TimelineTools.Create(new AgentMemoryStore(), new ContextStore());
            var text = await tool.InvokeAsync(new Dictionary<string, object?>
            {
                ["incidentId"] = entry.Id,
                ["limit"] = 300
            }, CancellationToken.None);
            OpsTimelineText = text?.ToString() ?? "(无时间线)";
        }
        catch (Exception ex)
        {
            OpsTimelineText = "读取时间线失败: " + ex.Message;
        }
    }

    public async Task LoadOpsAssetProfileAsync()
    {
        var assetId = (OpsAssetId ?? string.Empty).Trim();
        if (assetId.Length == 0)
        {
            StatusMessage = "请输入资产 ID（如 ssh:web01）";
            return;
        }
        try
        {
            OpsAssetProfileText = await AssetProfileService.BuildAsync(new AgentMemoryStore(),
                _configService, assetId, 10);
            StatusMessage = $"已加载资产画像: {assetId}";
        }
        catch (Exception ex)
        {
            OpsAssetProfileText = "读取资产画像失败: " + ex.Message;
        }
    }
}

/// <summary>事件浏览列表行。</summary>
public sealed class IncidentInsightEntry
{
    public long Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string AssetIds { get; init; } = string.Empty;
    public string UpdatedAt { get; init; } = string.Empty;
    public string RootCause { get; init; } = string.Empty;
    public string Symptom { get; init; } = string.Empty;

    public string DisplayTitle => $"[{Status}] {Title}";
    public string Meta => $"#{Id} · {AssetIds} · {UpdatedAt}";
    public string Preview => (string.IsNullOrWhiteSpace(RootCause) ? Symptom : RootCause) is { Length: > 0 } text
        ? (text.Length <= 160 ? text : text[..160] + "…")
        : "-";

    public static IncidentInsightEntry From(IncidentRecord r) => new()
    {
        Id = r.Id,
        Title = r.Title,
        Status = r.Status,
        AssetIds = r.AssetIds,
        UpdatedAt = r.UpdatedAt,
        RootCause = r.RootCause,
        Symptom = r.Symptom
    };
}
