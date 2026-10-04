using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

/// <summary>
/// 历史快照查看（列表 → 详情，可切换服务器）。读取本机快照库 snapshots.db，不连服务器。
/// </summary>
public class SnapshotHistoryViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService;
    private readonly ISnapshotStore _store;

    private SshServerConfig? _selectedServer;
    private SnapshotRow? _selectedRow;
    private string _summary = string.Empty;
    private string _rawJson = string.Empty;
    private string _statusMessage = string.Empty;

    public SnapshotHistoryViewModel(ISnapshotStore store, IConfigService configService)
    {
        _store = store;
        _configService = configService;
    }

    public ObservableCollection<SshServerConfig> Servers { get; } = new();
    public ObservableCollection<SnapshotRow> Snapshots { get; } = new();
    public ObservableCollection<SnapshotSectionRow> Sections { get; } = new();
    public ObservableCollection<SnapshotEventRow> Events { get; } = new();

    public SshServerConfig? SelectedServer
    {
        get => _selectedServer;
        set => Set(ref _selectedServer, value);
    }

    public SnapshotRow? SelectedRow
    {
        get => _selectedRow;
        set => Set(ref _selectedRow, value);
    }

    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public string RawJson { get => _rawJson; set => Set(ref _rawJson, value); }
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    public async Task InitializeAsync(string? initialServerId)
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            Servers.Clear();
            foreach (var server in config.Servers)
                Servers.Add(server);

            SelectedServer = Servers.FirstOrDefault(s => s.Id == initialServerId) ?? Servers.FirstOrDefault();
            StatusMessage = Servers.Count == 0 ? "没有已配置的服务器" : $"共 {Servers.Count} 台服务器";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载服务器失败: {ex.Message}";
        }
    }

    /// <summary>加载当前服务器的快照列表；<paramref name="selectSnapshotId"/> 指定要定位的历史快照（默认最新一份）。</summary>
    public async Task LoadSnapshotsAsync(long? selectSnapshotId = null)
    {
        Snapshots.Clear();
        ClearDetail();

        if (SelectedServer == null)
        {
            StatusMessage = "请选择服务器";
            return;
        }

        try
        {
            var list = await _store.GetRecentAsync(SelectedServer.Id, 50);
            foreach (var snapshot in list)
                Snapshots.Add(new SnapshotRow(snapshot));

            StatusMessage = list.Count == 0
                ? $"{SelectedServer.Name} 还没有快照 (可在服务器列表右键「采集快照」生成)"
                : $"{SelectedServer.Name}: 共 {list.Count} 份快照";

            SelectedRow = (selectSnapshotId is > 0 ? Snapshots.FirstOrDefault(r => r.Id == selectSnapshotId) : null)
                          ?? Snapshots.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载快照失败: {ex.Message}";
        }
    }

    /// <summary>渲染当前选中快照的概览/事件/原始数据。</summary>
    public async Task ShowSelectedAsync()
    {
        ClearDetail();
        if (SelectedRow == null)
            return;

        // 列表来自 GetRecentAsync(轻量, 刻意不含 DataJson), 详情必须按 Id 取完整记录, 否则数据为空。
        var snapshot = await _store.GetByIdAsync(SelectedRow.Id) ?? SelectedRow.Snapshot;

        var state = snapshot.Status.ToString().ToLowerInvariant();
        var createdText = FormatLocal(snapshot.CreatedAt);
        Summary = $"{state} | 开始 {createdText} | 耗时 {snapshot.DurationMs:F0}ms | "
                  + $"提权 {snapshot.Escalation ?? "-"} | collectorVersion {snapshot.CollectorVersion}"
                  + (string.IsNullOrWhiteSpace(snapshot.Error) ? string.Empty : $"\n错误: {snapshot.Error}");

        RawJson = Pretty(snapshot.DataJson);
        ParseSections(snapshot.DataJson);

        try
        {
            var events = await _store.GetEventsAsync(snapshot.Id, 200);
            foreach (var e in events)
                Events.Add(new SnapshotEventRow(e));
        }
        catch
        {
            // 事件读取失败不影响详情主体
        }
    }

    private void ClearDetail()
    {
        Sections.Clear();
        Events.Clear();
        Summary = string.Empty;
        RawJson = string.Empty;
    }

    private void ParseSections(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
            return;

        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            if (!doc.RootElement.TryGetProperty("sections", out var sections) || sections.ValueKind != JsonValueKind.Object)
                return;

            foreach (var section in sections.EnumerateObject())
            {
                var value = section.Value;
                var status = value.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
                var duration = value.TryGetProperty("durationMs", out var dm) && dm.ValueKind == JsonValueKind.Number ? dm.GetDouble() : 0;
                var error = value.TryGetProperty("error", out var er) ? er.GetString() : null;
                var note = value.TryGetProperty("note", out var nt) ? nt.GetString() : null;
                var noteText = string.Join("; ", new[] { error, note }.Where(x => !string.IsNullOrWhiteSpace(x)));
                Sections.Add(new SnapshotSectionRow(section.Name, status, duration, noteText));
            }
        }
        catch (JsonException)
        {
            // 数据损坏时仅保留原始 JSON 供人工查看
        }
    }

    private static string Pretty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "(本次快照无数据)";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string FormatLocal(string? iso)
    {
        return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : (iso ?? string.Empty);
    }

    public sealed class SnapshotRow
    {
        public SnapshotRow(ServerSnapshot snapshot) => Snapshot = snapshot;
        public ServerSnapshot Snapshot { get; }
        public long Id => Snapshot.Id;
        public string CreatedAtText => FormatLocal(Snapshot.CreatedAt);
        public string State => Snapshot.Status.ToString().ToLowerInvariant();
        public double DurationMs => Snapshot.DurationMs;
        public string Escalation => string.IsNullOrEmpty(Snapshot.Escalation) ? "-" : Snapshot.Escalation;
        public string Error => Snapshot.Error ?? string.Empty;
    }

    public sealed class SnapshotSectionRow
    {
        public SnapshotSectionRow(string dimension, string status, double durationMs, string note)
        {
            Dimension = dimension;
            Status = status;
            DurationMs = durationMs;
            Note = note;
        }

        public string Dimension { get; }
        public string Status { get; }
        public double DurationMs { get; }
        public string Note { get; }
    }

    public sealed class SnapshotEventRow
    {
        public SnapshotEventRow(SnapshotEvent e)
        {
            Timestamp = FormatLocal(e.Timestamp);
            Kind = e.Kind;
            Collector = e.Collector ?? string.Empty;
            Message = e.Message ?? string.Empty;
        }

        public string Timestamp { get; }
        public string Kind { get; }
        public string Collector { get; }
        public string Message { get; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
