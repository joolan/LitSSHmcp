using System.IO;
using System.Text.Json;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services.Backup;

public sealed class BackupState
{
    public DateTime? LastRunAt { get; set; }
    public string? LastResult { get; set; }
    public string? LastBackupFile { get; set; }
    public string? LastUploadResult { get; set; }
}

public static class BackupStateStore
{
    private sealed class FileModel
    {
        public Dictionary<string, BackupState> Plans { get; set; } = new(StringComparer.Ordinal);
    }

    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(ConfigPaths.AppDataDir, "backup-state.json");

    public static BackupState? Get(string planId)
    {
        lock (Gate)
        {
            var model = Load();
            return model.Plans.TryGetValue(planId, out var state) ? state : null;
        }
    }

    public static void Save(string planId, BackupState state)
    {
        lock (Gate)
        {
            var model = Load();
            model.Plans[planId] = state;
            try
            {
                Directory.CreateDirectory(ConfigPaths.AppDataDir);
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // 状态持久化失败不影响备份本身
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
