using System.IO;
using System.Text.Json;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 命令片段存储：
///  - 全局片段：%APPDATA%\LitSSH\snippets.json
///  - 每服务器片段：%APPDATA%\LitSSH\server-snippets.json（字典：serverId -> 片段列表）
/// </summary>
public static class SnippetStore
{
    private static readonly string GlobalPath = Path.Combine(ConfigPaths.AppDataDir, "snippets.json");
    private static readonly string ServerPath = Path.Combine(ConfigPaths.AppDataDir, "server-snippets.json");

    // ---- 全局 ----

    public static List<CommandSnippet> LoadGlobal()
    {
        try
        {
            if (File.Exists(GlobalPath))
            {
                var list = JsonSerializer.Deserialize<List<CommandSnippet>>(File.ReadAllText(GlobalPath));
                if (list is not null)
                    return list;
            }
        }
        catch
        {
            // 损坏则回退默认
        }
        return Defaults();
    }

    public static void SaveGlobal(IEnumerable<CommandSnippet> snippets)
        => SaveList(GlobalPath, snippets);

    // ---- 每服务器 ----

    public static List<CommandSnippet> LoadServer(string serverId)
    {
        if (string.IsNullOrEmpty(serverId))
            return new List<CommandSnippet>();
        return LoadMap().TryGetValue(serverId, out var list) ? list : new List<CommandSnippet>();
    }

    public static void SaveServer(string serverId, IEnumerable<CommandSnippet> snippets)
    {
        if (string.IsNullOrEmpty(serverId))
            return;
        var map = LoadMap();
        map[serverId] = snippets.Where(s => !string.IsNullOrWhiteSpace(s.Command)).ToList();
        try
        {
            var dir = Path.GetDirectoryName(ServerPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(ServerPath, JsonSerializer.Serialize(map));
        }
        catch
        {
            // 保存失败忽略
        }
    }

    private static Dictionary<string, List<CommandSnippet>> LoadMap()
    {
        try
        {
            if (File.Exists(ServerPath))
                return JsonSerializer.Deserialize<Dictionary<string, List<CommandSnippet>>>(File.ReadAllText(ServerPath)) ?? new();
        }
        catch
        {
            // 损坏则空
        }
        return new();
    }

    private static void SaveList(string path, IEnumerable<CommandSnippet> snippets)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var list = snippets.Where(s => !string.IsNullOrWhiteSpace(s.Command)).ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(list));
        }
        catch
        {
            // 保存失败忽略
        }
    }

    private static List<CommandSnippet> Defaults() => new()
    {
        new CommandSnippet("查看磁盘", "df -h"),
        new CommandSnippet("查看内存", "free -m"),
        new CommandSnippet("查看负载", "uptime"),
        new CommandSnippet("占用端口", "ss -ltnp"),
        new CommandSnippet("最近日志", "journalctl -n 100 --no-pager"),
        new CommandSnippet("Docker 容器", "docker ps --format 'table {{.Names}}\\t{{.Status}}\\t{{.Ports}}'")
    };
}
