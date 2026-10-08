using System.Text.Json;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.Sync;

public sealed class SyncState
{
    public DateTime? LastRunAt { get; set; }
    public string? LastResult { get; set; }
}

/// <summary>
/// 同步任务的上次运行状态（时间/结果）持久化到 %APPDATA%\LitSSH\sync-state.json。
/// 用于 UI 展示与「固定间隔」的基准（重启后仍延续）。
/// </summary>
public static class SyncStateStore
{
    private sealed class FileModel
    {
        public Dictionary<string, SyncState> Tasks { get; set; } = new(StringComparer.Ordinal);
    }

    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(ConfigPaths.AppDataDir, "sync-state.json");

    public static SyncState? Get(string taskId)
    {
        lock (Gate)
        {
            var model = Load();
            return model.Tasks.TryGetValue(taskId, out var s) ? s : null;
        }
    }

    public static void Save(string taskId, DateTime lastRunAt, string? lastResult)
    {
        lock (Gate)
        {
            var model = Load();
            model.Tasks[taskId] = new SyncState { LastRunAt = lastRunAt, LastResult = lastResult };
            try
            {
                Directory.CreateDirectory(ConfigPaths.AppDataDir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // 持久化失败忽略
            }
        }
    }

    private static FileModel Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new FileModel();
            return JsonSerializer.Deserialize<FileModel>(File.ReadAllText(FilePath)) ?? new FileModel();
        }
        catch
        {
            return new FileModel();
        }
    }
}
