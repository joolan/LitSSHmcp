using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media;
using LitSSHmcp.App.Controls;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

/// <summary>快照趋势：从某服务器最近若干份成功快照的 resource 维度提取指标，画内存/磁盘/Swap/负载趋势。</summary>
public sealed class SnapshotTrendViewModel : INotifyPropertyChanged
{
    private readonly ISnapshotStore _store;
    private readonly IConfigService _config;

    public SnapshotTrendViewModel(ISnapshotStore store, IConfigService config)
    {
        _store = store;
        _config = config;
    }

    public ObservableCollection<SshServerConfig> Servers { get; } = new();

    private SshServerConfig? _selectedServer;
    public SshServerConfig? SelectedServer { get => _selectedServer; set => Set(ref _selectedServer, value); }

    private string _status = string.Empty;
    public string Status { get => _status; set => Set(ref _status, value); }

    public IReadOnlyList<TrendSeries> Series { get; private set; } = Array.Empty<TrendSeries>();
    public IReadOnlyList<DateTime> Times { get; private set; } = Array.Empty<DateTime>();

    private static readonly Color MemColor = Color.FromRgb(0x4C, 0x8B, 0xF5);
    private static readonly Color DiskColor = Color.FromRgb(0xE0, 0x9B, 0x2E);
    private static readonly Color SwapColor = Color.FromRgb(0x9C, 0x6B, 0xD6);
    private static readonly Color LoadColor = Color.FromRgb(0x38, 0xB2, 0x6B);

    public async Task InitializeAsync(string? serverId)
    {
        try
        {
            var cfg = await _config.LoadConfigAsync();
            Servers.Clear();
            foreach (var s in cfg.Servers)
                Servers.Add(s);
            _selectedServer = Servers.FirstOrDefault(s => s.Id == serverId) ?? Servers.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedServer));
        }
        catch (Exception ex)
        {
            Status = "加载服务器失败: " + ex.Message;
        }
    }

    public async Task LoadAsync()
    {
        if (SelectedServer is null)
        {
            Series = Array.Empty<TrendSeries>();
            Times = Array.Empty<DateTime>();
            Status = "请选择服务器";
            Notify();
            return;
        }

        var points = new List<TrendPoint>();
        try
        {
            var list = await _store.GetRecentAsync(SelectedServer.Id, 50);
            foreach (var snap in list.Where(s => s.Status == SnapshotStatus.Succeeded))
            {
                var full = await _store.GetByIdAsync(snap.Id);
                if (full?.DataJson is null)
                    continue;
                var point = Extract(full);
                if (point is not null)
                    points.Add(point);
            }
        }
        catch (Exception ex)
        {
            Status = "读取快照失败: " + ex.Message;
            Notify();
            return;
        }

        points.Sort((a, b) => a.Time.CompareTo(b.Time));   // GetRecentAsync 为倒序 → 转正序

        Times = points.Select(p => p.Time).ToList();
        Series = new List<TrendSeries>
        {
            new("内存%", MemColor, points.Select(p => p.Mem).ToList()),
            new("磁盘%(最大分区)", DiskColor, points.Select(p => p.Disk).ToList()),
            new("Swap%", SwapColor, points.Select(p => p.Swap).ToList()),
            new("负载%(1m/核)", LoadColor, points.Select(p => p.LoadPct).ToList()),
        }.Where(s => s.Values.Any(v => v is not null)).ToList();

        Status = points.Count == 0
            ? $"{SelectedServer.Name}: 无可解析的资源快照（需成功快照且含 resource 维度）"
            : $"{SelectedServer.Name}: {points.Count} 个数据点（来自最近 50 份成功快照）";
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(Series));
        OnPropertyChanged(nameof(Times));
    }

    private sealed record TrendPoint(DateTime Time, double? Mem, double? Disk, double? Swap, double? LoadPct);

    private static TrendPoint? Extract(ServerSnapshot snap)
    {
        try
        {
            using var doc = JsonDocument.Parse(snap.DataJson!);
            if (!doc.RootElement.TryGetProperty("sections", out var sections) ||
                !sections.TryGetProperty("resource", out var res) ||
                !res.TryGetProperty("data", out var data))
                return null;

            double? mem = null, swap = null, disk = null, loadPct = null;

            if (data.TryGetProperty("memory", out var memory))
            {
                mem = Num(memory, "usedPercent");
                swap = Num(memory, "swapUsedPercent");
            }
            if (data.TryGetProperty("disks", out var disks) && disks.ValueKind == JsonValueKind.Array)
            {
                double max = 0;
                var any = false;
                foreach (var diskEl in disks.EnumerateArray())
                {
                    var used = Num(diskEl, "usedPercent");
                    if (used is not null)
                    {
                        max = Math.Max(max, used.Value);
                        any = true;
                    }
                }
                if (any)
                    disk = max;
            }
            if (data.TryGetProperty("load", out var load) && load.TryGetProperty("1m", out var l1) && l1.ValueKind == JsonValueKind.Number)
            {
                var cores = data.TryGetProperty("cpu", out var cpu) ? Num(cpu, "cores") ?? 0 : 0;
                if (cores > 0)
                    loadPct = Math.Round(l1.GetDouble() / cores * 100, 1);
            }

            if (mem is null && disk is null && swap is null && loadPct is null)
                return null;

            var time = DateTime.TryParse(snap.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToLocalTime()
                : DateTime.MinValue;
            return new TrendPoint(time, mem, disk, swap, loadPct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? Num(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
