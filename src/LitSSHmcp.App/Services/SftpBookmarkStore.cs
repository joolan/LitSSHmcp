using System.IO;
using System.Text.Json;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services;

/// <summary>每服务器的 SFTP 目录书签（%APPDATA%\LitSSH\sftp-bookmarks.json）。</summary>
public static class SftpBookmarkStore
{
    private static readonly string FilePath = Path.Combine(ConfigPaths.AppDataDir, "sftp-bookmarks.json");

    public static List<string> Load(string serverId)
    {
        if (string.IsNullOrEmpty(serverId))
            return new List<string>();
        var map = LoadMap();
        return map.TryGetValue(serverId, out var list) ? list : new List<string>();
    }

    public static void Save(string serverId, IEnumerable<string> bookmarks)
    {
        if (string.IsNullOrEmpty(serverId))
            return;
        var map = LoadMap();
        map[serverId] = bookmarks.Distinct().ToList();
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(map));
        }
        catch
        {
            // 保存失败忽略
        }
    }

    private static Dictionary<string, List<string>> LoadMap()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            // 损坏则空
        }
        return new();
    }
}
