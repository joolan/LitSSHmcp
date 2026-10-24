using System.Globalization;
using System.Windows.Data;

namespace LitSSHmcp.App.Controls;

/// <summary>管理列表展示用：把内部枚举/标识转成中文可读文本。</summary>
public sealed class ServerAuthTypeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString()?.ToLowerInvariant() switch
        {
            "password" => "密码",
            "keyfile" or "key" => "密钥文件",
            var text when !string.IsNullOrWhiteSpace(text) => value!,
            _ => "—"
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class DatasourceTypeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString()?.ToLowerInvariant() switch
        {
            "mysql" => "MySQL",
            "redis" => "Redis",
            "postgres" or "postgresql" => "PostgreSQL",
            var text when !string.IsNullOrWhiteSpace(text) => value!,
            _ => "—"
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class AccessModeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString()?.ToLowerInvariant() switch
        {
            "direct" => "直连",
            "sshtunnel" or "ssh_tunnel" => "SSH 隧道",
            var text when !string.IsNullOrWhiteSpace(text) => value!,
            _ => "—"
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime time)
            return "—";

        var local = time.Kind == DateTimeKind.Utc ? time.ToLocalTime() : time;
        var delta = DateTime.Now - local;
        if (delta.TotalMinutes < 1) return "刚刚";
        if (delta.TotalHours < 1) return $"{(int)delta.TotalMinutes} 分钟前";
        if (delta.TotalDays < 1) return $"{(int)delta.TotalHours} 小时前";
        if (delta.TotalDays < 30) return $"{(int)delta.TotalDays} 天前";
        return local.ToString("yyyy-MM-dd");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
