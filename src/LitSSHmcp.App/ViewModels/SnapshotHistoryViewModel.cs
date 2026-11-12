using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Snapshot;
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
    private string _accountsSummary = string.Empty;
    private string _diffText = string.Empty;
    private string _healthText = string.Empty;

    public SnapshotHistoryViewModel(ISnapshotStore store, IConfigService configService)
    {
        _store = store;
        _configService = configService;
        CompareCommand = new RelayCommand(_ => _ = CompareWithPreviousAsync(), _ => SelectedRow != null);
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
    public string AccountsSummary { get => _accountsSummary; set => Set(ref _accountsSummary, value); }
    public string DiffText { get => _diffText; set => Set(ref _diffText, value); }
    public string HealthText { get => _healthText; set => Set(ref _healthText, value); }
    public ICommand CompareCommand { get; }

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
        AccountsSummary = BuildAccountsSummary(snapshot.DataJson);
        HealthText = BuildHealthSummary(SelectedServer?.Name, snapshot.DataJson);
        DiffText = string.Empty;
        (CompareCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
        AccountsSummary = string.Empty;
        HealthText = string.Empty;
        DiffText = string.Empty;
        (CompareCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

    private async Task CompareWithPreviousAsync()
    {
        if (SelectedRow is null)
            return;
        var index = Snapshots.IndexOf(SelectedRow);
        if (index < 0 || index + 1 >= Snapshots.Count)
        {
            StatusMessage = "没有更早的快照可对比。";
            return;
        }

        try
        {
            var currentRow = Snapshots[index];
            var previousRow = Snapshots[index + 1];
            var current = await _store.GetByIdAsync(currentRow.Id) ?? currentRow.Snapshot;
            var previous = await _store.GetByIdAsync(previousRow.Id) ?? previousRow.Snapshot;
            if (string.IsNullOrWhiteSpace(current.DataJson) || string.IsNullOrWhiteSpace(previous.DataJson))
            {
                StatusMessage = "所选快照缺少数据，无法对比。";
                return;
            }

            var diff = SnapshotDiffService.Compare(previous.DataJson, current.DataJson);
            DiffText = JsonSerializer.Serialize(diff, new JsonSerializerOptions { WriteIndented = true });
            StatusMessage = $"对比 #{previous.Id} → #{current.Id}";
        }
        catch (Exception ex)
        {
            StatusMessage = "对比失败: " + ex.Message;
        }
    }

    private static string BuildHealthSummary(string? serverName, string? dataJson)
    {
        var health = ServerHealthService.Evaluate(dataJson, serverName);
        var lines = new List<string> { health.Summary };
        if (health.Findings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("主要发现:");
            lines.AddRange(health.Findings.Take(12).Select(f => $"- [{f.Severity}] {f.Title}: {f.Detail}"));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildAccountsSummary(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
            return "(本次快照无数据)";
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            if (!doc.RootElement.TryGetProperty("sections", out var sections) ||
                !sections.TryGetProperty("accounts", out var accounts))
                return "本次快照未采集 accounts 维度（可能已关闭或旧版本）。";

            var status = accounts.TryGetProperty("status", out var st) ? st.GetString() : null;
            if (string.Equals(status, "skipped", StringComparison.OrdinalIgnoreCase))
            {
                var note = accounts.TryGetProperty("note", out var nt) ? nt.GetString() : null;
                return "accounts 已跳过: " + (note ?? "未采集");
            }

            if (!accounts.TryGetProperty("data", out var data))
                return "accounts 无数据。";

            var lines = new List<string>();
            if (data.TryGetProperty("summary", out var summary))
            {
                lines.Add($"用户 {ReadInt(summary, "users")}，风险用户 {ReadInt(summary, "riskyUsers")}，" +
                          $"公钥 {ReadInt(summary, "authorizedKeys")}，失败登录用户 {ReadInt(summary, "failedUsers")}，" +
                          $"findings {ReadInt(summary, "findings")}（high {ReadInt(summary, "high")} / medium {ReadInt(summary, "medium")}）");
            }
            lines.Add("shadow 可读: " + (data.TryGetProperty("shadowReadable", out var sr) && sr.ValueKind == JsonValueKind.True ? "是" : "否"));

            if (data.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array)
            {
                var risky = users.EnumerateArray()
                    .Where(u => u.TryGetProperty("riskLevel", out var r) &&
                                !string.Equals(r.GetString(), "正常", StringComparison.Ordinal))
                    .Take(20)
                    .Select(u =>
                    {
                        var name = u.TryGetProperty("name", out var n) ? n.GetString() : "?";
                        var risk = u.TryGetProperty("riskLevel", out var r) ? r.GetString() : "?";
                        var notes = u.TryGetProperty("riskNotes", out var notesNode) && notesNode.ValueKind == JsonValueKind.Array
                            ? string.Join("；", notesNode.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)))
                            : string.Empty;
                        return $"{name} [{risk}] {(string.IsNullOrWhiteSpace(notes) ? string.Empty : notes)}";
                    })
                    .ToList();
                if (risky.Count > 0)
                {
                    lines.Add(string.Empty);
                    lines.Add("风险账号:");
                    lines.AddRange(risky);
                }
            }

            if (data.TryGetProperty("findings", out var findings) && findings.ValueKind == JsonValueKind.Array)
            {
                var top = findings.EnumerateArray().Take(10).Select(f =>
                {
                    var severity = f.TryGetProperty("severity", out var s) ? s.GetString() : "?";
                    var title = f.TryGetProperty("title", out var t) ? t.GetString() : "?";
                    return $"- [{severity}] {title}";
                }).ToList();
                if (top.Count > 0)
                {
                    lines.Add(string.Empty);
                    lines.Add("主要发现:");
                    lines.AddRange(top);
                }
            }

            return string.Join(Environment.NewLine, lines);
        }
        catch (JsonException)
        {
            return "accounts 数据解析失败。";
        }
    }

    private static int ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number) ? number : 0;

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
