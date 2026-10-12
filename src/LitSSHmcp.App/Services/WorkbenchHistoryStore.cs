using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services;

/// <summary>数据库工作台的查询历史（本机 JSON 持久化，按最近使用排序）。</summary>
public static class WorkbenchHistoryStore
{
    private const int MaxItems = 200;

    private static string FilePath => Path.Combine(ConfigPaths.AppDataDir, "workbench-history.json");

    public static List<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new List<string>();
            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            var list = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            return list.Where(s => !string.IsNullOrWhiteSpace(s)).Take(MaxItems).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static void Save(IEnumerable<string> items)
    {
        try
        {
            Directory.CreateDirectory(ConfigPaths.AppDataDir);
            var json = JsonSerializer.Serialize(items.Take(MaxItems).ToList(), new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(FilePath, json, new UTF8Encoding(false));
        }
        catch
        {
            // 保存失败忽略（历史非关键）
        }
    }
}
