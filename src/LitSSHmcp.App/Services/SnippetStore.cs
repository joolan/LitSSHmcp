using System.IO;
using System.Text.Json;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services;

/// <summary>命令片段存储（%APPDATA%\LitSSH\snippets.json）。</summary>
public static class SnippetStore
{
    private static readonly string FilePath = Path.Combine(ConfigPaths.AppDataDir, "snippets.json");

    public static List<CommandSnippet> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var list = JsonSerializer.Deserialize<List<CommandSnippet>>(File.ReadAllText(FilePath));
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

    public static void Save(IEnumerable<CommandSnippet> snippets)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var list = snippets.Where(s => !string.IsNullOrWhiteSpace(s.Command)).ToList();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
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
