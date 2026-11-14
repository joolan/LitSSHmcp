using System.IO;
using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services.Backup;

public enum BackupItemKind
{
    File,
    Directory,
    SqliteDatabase
}

public sealed class BackupItemDescriptor
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required BackupItemKind Kind { get; init; }
    public required Func<AppConfig, string, string> ResolvePath { get; init; }
}

public sealed class BackupItemInfo
{
    public required BackupItemDescriptor Descriptor { get; init; }
    public required string Path { get; init; }
    public required bool Exists { get; init; }
    public required long Size { get; init; }
    public string SizeText => BackupFormat.Size(Size);

    public string Id => Descriptor.Id;
    public string Name => Descriptor.Name;
    public string Description => Descriptor.Description;
}

public sealed class BackupItemOption
{
    public required BackupItemInfo Item { get; init; }
    public bool IsSelected { get; set; }
    public string Id => Item.Id;
    public string Name => Item.Name;
    public string SizeText => Item.SizeText;
    public string Description => Item.Description;
}

public static class BackupItemCatalog
{
    public static IReadOnlyList<BackupItemDescriptor> Descriptors { get; } = new[]
    {
        FileItem("config", "配置 config.json", "服务器、工具与安全配置", "config.json"),
        SqliteItem("agent-db", "AI 会话 / 记忆 agent.db", "会话、消息、记忆与队列", "agent.db"),
        SqliteItem("audit-db", "审计 audit.db", "命令审计与哈希链", "audit.db"),
        SqliteItem("snapshot-db", "快照 snapshots.db", "服务器快照与历史", "snapshots.db"),
        FileItem("notifications-inbox", "通知中心 notifications.json", "通知收件箱、已读状态与消息类型", "notifications.json"),
        SqliteItem("notifications-outbox", "通知投递 notifications.db", "通知渠道投递队列、重试与历史", "notifications.db"),
        DirItem("skills", "技能 skills", "本机技能文件", "skills"),
        DirItem("logs", "日志 logs", "应用运行日志（可能含敏感信息，默认不选）", "logs"),
        DirItem("terminal-logs", "终端会话日志 terminal-logs", "交互式终端录像与文本日志（默认不选）", "terminal-logs"),
        DirItem("vaults", "加密文件柜", "加密文件柜目录", "vaults"),
        DirItem("password-vault", "密码保管箱", "密码保管箱目录", "password-vault"),
        DirItem("pet-assets", "桌面宠物素材", "自定义宠物素材包、动画与许可证文件", "pet-assets"),
        new BackupItemDescriptor
        {
            Id = "spills",
            Name = "大结果 spills",
            Description = "Agent 大结果文件",
            Kind = BackupItemKind.Directory,
            ResolvePath = (config, dataDir) =>
            {
                var dir = config.Agent?.SpillDir;
                return string.IsNullOrWhiteSpace(dir) ? Path.Combine(dataDir, "spills") : dir!;
            }
        },
        new BackupItemDescriptor
        {
            Id = "workspace",
            Name = "工作区 jobs",
            Description = "Agent 工作区会话目录",
            Kind = BackupItemKind.Directory,
            ResolvePath = (config, _) =>
                Path.Combine(WorkspaceTools.ResolveDir(config.Agent?.WorkspaceDir),
                    WorkspaceLayout.JobsDirName)
        }
    };

    public static IReadOnlyList<BackupItemInfo> Build(AppConfig config, string dataDir,
        IEnumerable<string>? ids = null)
    {
        var result = new List<BackupItemInfo>();
        var wanted = ids?.ToHashSet(StringComparer.Ordinal);
        foreach (var descriptor in Descriptors)
        {
            if (wanted is not null && !wanted.Contains(descriptor.Id))
                continue;
            string path;
            try { path = descriptor.ResolvePath(config, dataDir); }
            catch { path = string.Empty; }
            var exists = !string.IsNullOrWhiteSpace(path) &&
                         (File.Exists(path) || Directory.Exists(path));
            result.Add(new BackupItemInfo
            {
                Descriptor = descriptor,
                Path = path,
                Exists = exists,
                Size = exists ? Measure(path) : 0
            });
        }
        return result;
    }

    public static long Measure(string path)
    {
        try
        {
            if (File.Exists(path))
                return new FileInfo(path).Length;
            if (!Directory.Exists(path))
                return 0;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true
            };
            var total = 0L;
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                try { total += new FileInfo(file).Length; } catch { /* ignore */ }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    private static BackupItemDescriptor FileItem(string id, string name, string description, string fileName) => new()
    {
        Id = id,
        Name = name,
        Description = description,
        Kind = BackupItemKind.File,
        ResolvePath = (_, dataDir) => Path.Combine(dataDir, fileName)
    };

    private static BackupItemDescriptor SqliteItem(string id, string name, string description, string fileName) => new()
    {
        Id = id,
        Name = name,
        Description = description,
        Kind = BackupItemKind.SqliteDatabase,
        ResolvePath = (_, dataDir) => Path.Combine(dataDir, fileName)
    };

    private static BackupItemDescriptor DirItem(string id, string name, string description, string directoryName) => new()
    {
        Id = id,
        Name = name,
        Description = description,
        Kind = BackupItemKind.Directory,
        ResolvePath = (_, dataDir) => Path.Combine(dataDir, directoryName)
    };
}

public static class BackupFormat
{
    public static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.##} MB",
        >= 1024L => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} B"
    };
}
