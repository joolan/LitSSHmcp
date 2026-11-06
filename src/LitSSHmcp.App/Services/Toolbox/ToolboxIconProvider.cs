using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services.Toolbox;

/// <summary>程序 / 文件图标提取与磁盘缓存。失败时由界面回退到类型图标。</summary>
public static class ToolboxIconProvider
{
    private static readonly string CacheDir = Path.Combine(ConfigPaths.AppDataDir, "toolbox-icons");
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string?> Memory = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<string?> GetIconPathAsync(ToolboxItem item)
    {
        if (item.Type is not (ToolboxItemType.Application or ToolboxItemType.File))
            return null;

        var path = item.Target;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        string key;
        try
        {
            key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                path + "|" + File.GetLastWriteTimeUtc(path).Ticks)));
        }
        catch
        {
            return null;
        }

        lock (Gate)
        {
            if (Memory.TryGetValue(key, out var cached))
                return cached;
        }

        var cachePath = Path.Combine(CacheDir, key + ".png");
        if (File.Exists(cachePath))
        {
            lock (Gate) Memory[key] = cachePath;
            return cachePath;
        }

        var result = await Task.Run(() => Extract(path, key, cachePath)).ConfigureAwait(false);
        lock (Gate) Memory[key] = result;
        return result;
    }

    private static string? Extract(string path, string key, string cachePath)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null)
                return null;

            Directory.CreateDirectory(CacheDir);
            using var bitmap = icon.ToBitmap();
            bitmap.Save(cachePath, ImageFormat.Png);
            return cachePath;
        }
        catch
        {
            return null;
        }
    }
}
