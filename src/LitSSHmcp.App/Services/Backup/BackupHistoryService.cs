using System.IO;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.Services.Backup;

public sealed class BackupHistoryItem
{
    public required string Path { get; init; }
    public required string FileName { get; init; }
    public required DateTime ModifiedAt { get; init; }
    public required long Size { get; init; }
    public required bool Encrypted { get; init; }
    public string SizeText => BackupFormat.Size(Size);
    public string ModifiedText => ModifiedAt.ToString("yyyy-MM-dd HH:mm:ss");
    public string EncryptedText => Encrypted ? "加密" : "未加密";
}

public static class BackupHistoryService
{
    public static IReadOnlyList<BackupHistoryItem> List(BackupPlanConfig plan)
    {
        if (string.IsNullOrWhiteSpace(plan.TargetDirectory) ||
            !Directory.Exists(plan.TargetDirectory))
            return Array.Empty<BackupHistoryItem>();

        var prefix = PlanPrefix(plan);
        return Directory.EnumerateFiles(plan.TargetDirectory, prefix + "*")
            .Where(path => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".lsbak", StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new BackupHistoryItem
                {
                    Path = path,
                    FileName = info.Name,
                    ModifiedAt = info.LastWriteTime,
                    Size = info.Length,
                    Encrypted = BackupCrypto.IsEncrypted(path)
                };
            })
            .OrderByDescending(item => item.ModifiedAt)
            .ToArray();
    }

    private static string PlanPrefix(BackupPlanConfig plan)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string((plan.Name ?? string.Empty)
            .Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray()).Trim();
        if (name.Length == 0)
            name = "backup";
        return "LitSSH-backup-" + name + "-" +
               plan.Id[..Math.Min(8, plan.Id.Length)] + "-";
    }
}
