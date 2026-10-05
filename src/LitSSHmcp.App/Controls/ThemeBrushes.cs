using System.Windows;
using System.Windows.Media;

namespace LitSSHmcp.App.Controls;

/// <summary>
/// 从当前主题 token 解析画刷；无 Application / 无 token 时回退到内置默认（浅色），
/// 保证单元测试（无 Application）与设计期可用。
/// </summary>
public static class ThemeBrushes
{
    public static Brush TextPrimary => Pick("AppTextPrimaryBrush", Brushes.Black);
    public static Brush TextSecondary => Pick("AppTextSecondaryBrush", Brushes.Gray);
    public static Brush Surface => Pick("AppSurfaceBrush", Brushes.White);
    public static Brush Border => Pick("AppBorderBrush", new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)));
    public static Brush Accent => Pick("AppAccentBrush", Color.FromRgb(0x00, 0x67, 0xC0));
    public static Brush CodeBg => Pick("AppCodeBgBrush", new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6)));
    public static Brush CodeText => Pick("AppCodeTextBrush", Brushes.Black);
    public static Brush Success => Pick("AppSuccessBrush", Brushes.Green);
    public static Brush Warning => Pick("AppWarningBrush", Brushes.Orange);

    public static Brush Pick(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

    public static Brush Pick(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
