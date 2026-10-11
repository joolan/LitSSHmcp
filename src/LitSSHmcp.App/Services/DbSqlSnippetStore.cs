using System.IO;
using System.Text.Json;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 数据库 SQL 片段存储：按「数据源 → 库」两级分组，落盘
/// %APPDATA%\LitSSH\db-sql-snippets.json（{"datasourceId":{"database":[片段...]}}）。
/// 空数据库名以空字符串为键。
/// </summary>
public static class DbSqlSnippetStore
{
    private static readonly string Path = System.IO.Path.Combine(ConfigPaths.AppDataDir, "db-sql-snippets.json");

    public static List<DbSqlSnippet> Load(string? datasourceId, string? database)
    {
        if (string.IsNullOrEmpty(datasourceId))
            return new List<DbSqlSnippet>();

        var all = LoadAll();
        if (all.TryGetValue(datasourceId, out var byDb) &&
            byDb.TryGetValue(database ?? string.Empty, out var list))
            return list;
        return new List<DbSqlSnippet>();
    }

    public static void Save(string? datasourceId, string? database, IEnumerable<DbSqlSnippet> snippets)
    {
        if (string.IsNullOrEmpty(datasourceId))
            return;

        var all = LoadAll();
        if (!all.TryGetValue(datasourceId, out var byDb))
        {
            byDb = new Dictionary<string, List<DbSqlSnippet>>();
            all[datasourceId] = byDb;
        }

        var cleaned = snippets
            .Where(s => !string.IsNullOrWhiteSpace(s.Sql))
            .ToList();

        var key = database ?? string.Empty;
        if (cleaned.Count == 0)
            byDb.Remove(key);
        else
            byDb[key] = cleaned;

        SaveAll(all);
    }

    private static Dictionary<string, Dictionary<string, List<DbSqlSnippet>>> LoadAll()
    {
        try
        {
            if (File.Exists(Path))
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<DbSqlSnippet>>>>(
                    File.ReadAllText(Path));
                if (map is not null)
                    return map;
            }
        }
        catch
        {
            // 损坏则空
        }
        return new();
    }

    private static void SaveAll(Dictionary<string, Dictionary<string, List<DbSqlSnippet>>> all)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(Path, JsonSerializer.Serialize(all));
        }
        catch
        {
            // 保存失败忽略
        }
    }
}
