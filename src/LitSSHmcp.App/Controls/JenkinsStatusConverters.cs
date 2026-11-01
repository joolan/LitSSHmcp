using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Controls;

/// <summary>Jenkins 任务状态到图标/颜色的展示转换。</summary>
public sealed class JenkinsStatusIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString() switch
        {
            "queued" => SymbolRegular.History24,
            "building" => SymbolRegular.Flash24,
            "success" => SymbolRegular.ShieldCheckmark24,
            "failure" => SymbolRegular.Dismiss24,
            "aborted" => SymbolRegular.Stop24,
            "unstable" => SymbolRegular.Info24,
            "disabled" => SymbolRegular.Stop24,
            _ => SymbolRegular.Play24
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class JenkinsStatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value?.ToString() switch
        {
            "queued" => "AppAccentBrush",
            "building" => "AppWarningBrush",
            "success" => "AppSuccessBrush",
            "failure" => "AppDangerBrush",
            "aborted" => "AppTextSecondaryBrush",
            "unstable" => "AppWarningBrush",
            "disabled" => "AppTextTertiaryBrush",
            _ => "AppAccentBrush"
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
