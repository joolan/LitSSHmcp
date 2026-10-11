using System.Data;
using System.Globalization;
using System.Windows.Data;

namespace LitSSHmcp.App.Controls;

/// <summary>把数据库 NULL（DBNull/ null）显示为 <c>NULL</c>，其余按原值显示（仅影响显示，不影响复制/导出）。</summary>
public sealed class NullToNullTextConverter : IValueConverter
{
    public static readonly NullToNullTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null || value == DBNull.Value ? "NULL" : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
