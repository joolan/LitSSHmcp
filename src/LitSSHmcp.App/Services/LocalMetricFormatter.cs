using System.Globalization;

namespace LitSSHmcp.App.Services;

/// <summary>本机指标格式化：容量、速率、运行时长与百分比，供主页看板共用。</summary>
public static class LocalMetricFormatter
{
    private static readonly string[] ByteUnits = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Bytes(long bytes)
    {
        var value = Math.Max(0, bytes);
        var unit = 0;
        var scaled = (double)value;
        while (scaled >= 1024 && unit < ByteUnits.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }
        return string.Format(CultureInfo.InvariantCulture, "{0:0.#} {1}", scaled, ByteUnits[unit]);
    }

    public static string Rate(double bytesPerSecond)
    {
        var value = Math.Max(0, bytesPerSecond);
        var unit = 0;
        while (value >= 1024 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Format(CultureInfo.InvariantCulture, "{0:0.#} {1}/s", value, ByteUnits[unit]);
    }

    public static string Percent(double value) =>
        string.Format(CultureInfo.InvariantCulture, "{0:0.#}%", Math.Clamp(value, 0, 100));

    public static string Uptime(TimeSpan uptime)
    {
        if (uptime.TotalMinutes < 1)
            return "不足 1 分钟";
        if (uptime.TotalHours < 1)
            return $"{Math.Max(1, (int)uptime.TotalMinutes)} 分钟";
        if (uptime.TotalDays < 1)
            return $"{(int)uptime.TotalHours} 小时 {uptime.Minutes} 分";
        return $"{uptime.Days} 天 {uptime.Hours} 小时";
    }
}
