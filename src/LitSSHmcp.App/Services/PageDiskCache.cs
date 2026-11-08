using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 页面级轻量缓存（Jenkins/K8s 上次成功结果）。落盘失败不影响主流程。
/// </summary>
public static class PageDiskCache
{
    private sealed record Envelope<T>(DateTime SavedUtc, T Value);

    public sealed record ReadResult<T>(bool Found, T? Value, DateTime SavedUtc);

    private static readonly string RootDir = Path.Combine(ConfigPaths.AppDataDir, "page-cache");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<ReadResult<T>> TryLoadAsync<T>(string area, string key)
    {
        var path = PathFor(area, key);
        if (!File.Exists(path))
            return new ReadResult<T>(false, default, DateTime.MinValue);

        try
        {
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize<Envelope<T>>(json, Options);
            return envelope is null
                ? new ReadResult<T>(false, default, DateTime.MinValue)
                : new ReadResult<T>(true, envelope.Value, envelope.SavedUtc);
        }
        catch
        {
            return new ReadResult<T>(false, default, DateTime.MinValue);
        }
    }

    public static Task SaveAsync<T>(string area, string key, T value) => Task.Run(async () =>
    {
        try
        {
            var path = PathFor(area, key);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(new Envelope<T>(DateTime.UtcNow, value), Options);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // 缓存失败不阻断页面加载
        }
    });

    private static string PathFor(string area, string key)
    {
        var safeArea = Sanitize(area);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(RootDir, safeArea, hash + ".json");
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        return builder.Length == 0 ? "default" : builder.ToString();
    }
}
