using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;

namespace LitSSHmcp.App.ViewModels;

/// <summary>时间线中的一个快照点（展示用，保留原始点用于差异明细）。</summary>
public sealed class TimelinePointRow
{
    public TimelinePointRow(GraphTimelinePoint point)
    {
        Point = point;
        CapturedAtText = FormatTime(point.CapturedAt);
        SnapshotText = $"#{point.SnapshotId}";
        StateText = point.Success ? "成功" : "失败";
        NodeEdgeText = $"{point.Nodes.Length} 节点 / {point.Edges.Length} 边";
        DeltaText = $"+{point.AddedNodes.Length}/-{point.RemovedNodes.Length} 节点 · +{point.AddedEdges.Length}/-{point.RemovedEdges.Length} 边";
    }

    public GraphTimelinePoint Point { get; }
    public string CapturedAtText { get; }
    public string SnapshotText { get; }
    public string StateText { get; }
    public string NodeEdgeText { get; }
    public string DeltaText { get; }
    public bool HasChanges => Point.AddedNodes.Length + Point.RemovedNodes.Length
        + Point.AddedEdges.Length + Point.RemovedEdges.Length > 0;

    public static string FormatTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "-";
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : value.Replace('T', ' ');
    }
}

/// <summary>时间线差异明细行（节点/边的新增或移除）。</summary>
public sealed record TimelineChangeRow(string Kind, string Text, bool Added);

/// <summary>
/// 快照时间线：按服务器读取最近若干快照的图谱重放结果，展示每个时点的节点/边规模与相对上一份快照的变化。
/// 只读快照库，不连接服务器；与 MCP 的 topology_timeline / topology_diff 使用同一查询服务。
/// </summary>
public sealed class TopologyTimelineViewModel : INotifyPropertyChanged
{
    private readonly IAssetGraphQueryService _query;
    private readonly IConfigService _configService;

    private SshServerConfig? _selectedServer;
    private TimelinePointRow? _selectedPoint;
    private int _selectedLimit = 10;
    private string _summary = string.Empty;
    private string _statusMessage = string.Empty;

    public TopologyTimelineViewModel(IAssetGraphQueryService query, IConfigService configService)
    {
        _query = query;
        _configService = configService;
        LimitOptions = new[] { 5, 10, 20 };
    }

    public ObservableCollection<SshServerConfig> Servers { get; } = new();
    public ObservableCollection<TimelinePointRow> Points { get; } = new();
    public ObservableCollection<TimelineChangeRow> Changes { get; } = new();
    public IReadOnlyList<int> LimitOptions { get; }

    public SshServerConfig? SelectedServer
    {
        get => _selectedServer;
        set => Set(ref _selectedServer, value);
    }

    public TimelinePointRow? SelectedPoint
    {
        get => _selectedPoint;
        set
        {
            if (!Set(ref _selectedPoint, value))
                return;
            UpdateChanges(value);
        }
    }

    public int SelectedLimit
    {
        get => _selectedLimit;
        set => Set(ref _selectedLimit, value is 5 or 10 or 20 ? value : 10);
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public async Task InitializeAsync(string? initialServerId)
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            Servers.Clear();
            foreach (var server in config.Servers)
                Servers.Add(server);
            SelectedServer = Servers.FirstOrDefault(s => s.Id == initialServerId)
                ?? Servers.FirstOrDefault();
            StatusMessage = Servers.Count == 0 ? "没有已配置的服务器" : $"共 {Servers.Count} 台服务器";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载服务器失败: {ex.Message}";
        }
    }

    public async Task LoadAsync()
    {
        Points.Clear();
        Changes.Clear();
        Summary = string.Empty;

        if (SelectedServer is null)
        {
            StatusMessage = "请选择服务器";
            return;
        }

        try
        {
            StatusMessage = "正在读取快照时间线…";
            var result = await _query.GetTimelineAsync(SelectedServer.Id, SelectedLimit);
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                StatusMessage = result.Error;
                Summary = result.Summary;
                return;
            }

            foreach (var point in result.Points)
                Points.Add(new TimelinePointRow(point));
            Summary = result.Summary;
            SelectedPoint = Points.LastOrDefault();
            StatusMessage = Points.Count == 0
                ? $"{SelectedServer.Name} 还没有快照"
                : $"{SelectedServer.Name}: {Points.Count} 个时间点"
                  + (result.Truncated ? "（已按上限截断）" : string.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取时间线失败: {ex.Message}";
        }
    }

    public static IReadOnlyList<TimelineChangeRow> BuildChanges(GraphTimelinePoint point)
    {
        var rows = new List<TimelineChangeRow>();
        foreach (var id in point.AddedNodes)
            rows.Add(new TimelineChangeRow("节点 +", id, true));
        foreach (var id in point.RemovedNodes)
            rows.Add(new TimelineChangeRow("节点 -", id, false));
        foreach (var edge in point.AddedEdges)
            rows.Add(new TimelineChangeRow("关系 +", FormatEdge(edge), true));
        foreach (var edge in point.RemovedEdges)
            rows.Add(new TimelineChangeRow("关系 -", FormatEdge(edge), false));
        return rows;
    }

    private void UpdateChanges(TimelinePointRow? row)
    {
        Changes.Clear();
        if (row is null)
            return;
        foreach (var change in BuildChanges(row.Point))
            Changes.Add(change);
        if (Changes.Count == 0)
            Changes.Add(new TimelineChangeRow("无变化", "该快照相对上一份没有节点/关系变化", true));
    }

    private static string FormatEdge(string key)
    {
        var parts = key.Split('|', 3);
        return parts.Length == 3 ? $"{parts[0]} -{parts[1]}-> {parts[2]}" : key;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
