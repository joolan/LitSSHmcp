using System.Globalization;
using System.Windows.Data;

namespace LitSSHmcp.App.Controls;

/// <summary>把「隧道服务器 Id」转成简明标记：配置了隧道显示 <c>√</c>，否则显示空（仅影响列表显示）。</summary>
public sealed class TunnelMarkConverter : IValueConverter
{
    public static readonly TunnelMarkConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s) ? "√" : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
