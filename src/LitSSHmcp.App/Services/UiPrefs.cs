using System.IO;
using System.Text.Json;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services;

/// <summary>轻量 UI 偏好（如分栏宽度），持久化到 %APPDATA%\LitSSH\ui-prefs.json。</summary>
public static class UiPrefs
{
    private static readonly string FilePath = Path.Combine(ConfigPaths.AppDataDir, "ui-prefs.json");
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _cache;

    public static double GetDouble(string key, double fallback)
    {
        lock (Gate)
        {
            var map = Load();
            return map.TryGetValue(key, out var value) && double.TryParse(value, out var parsed) ? parsed : fallback;
        }
    }

    public static void SetDouble(string key, double value)
    {
        lock (Gate)
        {
            var map = Load();
            map[key] = value.ToString("0.##");
            Save(map);
        }
    }

    public static string GetString(string key, string fallback)
    {
        lock (Gate)
        {
            var map = Load();
            return map.TryGetValue(key, out var value) ? value : fallback;
        }
    }

    public static void SetString(string key, string value)
    {
        lock (Gate)
        {
            var map = Load();
            map[key] = value;
            Save(map);
        }
    }

    private static Dictionary<string, string> Load()
    {
        if (_cache is not null)
            return _cache;
        try
        {
            if (File.Exists(FilePath))
                _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new();
            else
                _cache = new();
        }
        catch
        {
            _cache = new();
        }
        return _cache;
    }

    private static void Save(Dictionary<string, string> map)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(map));
        }
        catch
        {
            // 偏好保存失败不影响运行
        }
    }
}
