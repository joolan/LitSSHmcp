using System.IO;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Topology;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 资产图谱提示词缓存：Agent 首开时把 Servers/DataSources/Applications/Relations 渲染成一大段文本，
/// 实测约 500ms。该内容只随 config.json 变化，因此按文件时间戳+长度缓存，避免每次会话首开重复生成。
/// </summary>
internal static class AssetGraphPromptCache
{
    private static readonly object Gate = new();
    private static string? _path;
    private static DateTime _lastWriteUtc;
    private static long _length;
    private static string? _value;
    private static bool _hasValue;

    public readonly record struct FileStamp(DateTime LastWriteUtc, long Length);

    public static FileStamp ReadStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new FileStamp(info.LastWriteTimeUtc, info.Length)
                : new FileStamp(DateTime.MinValue, -1);
        }
        catch
        {
            return new FileStamp(DateTime.MinValue, -1);
        }
    }

    /// <summary>命中当前文件版本的缓存。</summary>
    public static bool TryGet(string path, out string value)
    {
        var stamp = ReadStamp(path);
        lock (Gate)
        {
            if (_hasValue && _value is not null &&
                string.Equals(_path, path, StringComparison.OrdinalIgnoreCase) &&
                _lastWriteUtc == stamp.LastWriteUtc && _length == stamp.Length)
            {
                value = _value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    /// <summary>用“读取配置前”的时间戳写入，配置在格式化期间被改写时下次自然失效。</summary>
    public static void Store(string path, FileStamp stamp, string value)
    {
        lock (Gate)
        {
            _path = path;
            _lastWriteUtc = stamp.LastWriteUtc;
            _length = stamp.Length;
            _value = value;
            _hasValue = true;
        }
    }

    /// <summary>后台预热：格式化并写入缓存。</summary>
    public static void Prewarm(string path, AppConfig config)
    {
        try
        {
            var stamp = ReadStamp(path);
            var formatted = AssetGraphPromptFormatter.Format(config);
            if (formatted is null)
                return;
            Store(path, stamp, formatted);
        }
        catch
        {
            // 预热失败不影响正式路径
        }
    }
}
