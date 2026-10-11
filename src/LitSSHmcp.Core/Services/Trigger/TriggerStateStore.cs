using System.Text.Json;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.Trigger;

/// <summary>一次运行记录（供 UI 运行历史展示）。</summary>
public sealed class TriggerRunRecord
{
    public DateTime At { get; set; }
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string Cause { get; set; } = string.Empty;
}

public sealed class TriggerState
{
    public DateTime? LastRunAt { get; set; }
    public string? LastResult { get; set; }

    /// <summary>最近若干次运行记录（新的在前，最多 20 条）。</summary>
    public List<TriggerRunRecord> Runs { get; set; } = new();
}

/// <summary>
/// 触发事件运行状态持久化到 %APPDATA%\LitSSH\trigger-state.json。
/// 用于 UI 展示与「固定间隔」的基准（重启后仍延续）。
/// </summary>
public sealed class TriggerStateStore
{
    /// <summary>进程默认实例（真实 AppData 路径）。</summary>
    public static readonly TriggerStateStore Default = new();

    private sealed class FileModel
    {
        public Dictionary<string, TriggerState> Triggers { get; set; } = new(StringComparer.Ordinal);
    }

    private const int MaxRuns = 20;

    private readonly object _gate = new();
    private readonly string _filePath;

    public TriggerStateStore(string? filePath = null)
        => _filePath = filePath ?? Path.Combine(ConfigPaths.AppDataDir, "trigger-state.json");

    public TriggerState? Get(string triggerId)
    {
        lock (_gate)
        {
            var model = Load();
            return model.Triggers.TryGetValue(triggerId, out var s) ? s : null;
        }
    }

    public void Save(string triggerId, DateTime lastRunAt, string? lastResult,
        bool success, string summary, string cause)
    {
        lock (_gate)
        {
            var model = Load();
            if (!model.Triggers.TryGetValue(triggerId, out var state))
                state = new TriggerState();

            state.LastRunAt = lastRunAt;
            state.LastResult = lastResult;
            state.Runs.Insert(0, new TriggerRunRecord
            {
                At = lastRunAt,
                Success = success,
                Summary = summary,
                Cause = cause
            });
            if (state.Runs.Count > MaxRuns)
                state.Runs.RemoveRange(MaxRuns, state.Runs.Count - MaxRuns);
            model.Triggers[triggerId] = state;

            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_filePath, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // 持久化失败忽略
            }
        }
    }

    private FileModel Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new FileModel();
            return JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_filePath)) ?? new FileModel();
        }
        catch
        {
            return new FileModel();
        }
    }
}
