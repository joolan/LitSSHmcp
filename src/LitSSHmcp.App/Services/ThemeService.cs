using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 界面主题服务：解析 system/light/dark -> 应用 WPF-UI 主题并切换自研 token 字典；可附加自定义强调色。
/// </summary>
public static class ThemeService
{
    /// <summary>主题（含 token 字典）切换后触发，供自绘控件（如 MarkdownBox）重渲染。</summary>
    public static event Action? ThemeChanged;

    private static ResourceDictionary? _tokens;
    private static ResourceDictionary? _accentDict;
    private static bool _followSystem = true;
    private static string _accent = string.Empty;

    /// <summary>当前生效主题：light / dark。</summary>
    public static string Current { get; private set; } = "dark";

    public static void Apply(string? theme, string? accent = null)
    {
        _accent = accent ?? string.Empty;
        var t = (theme ?? "system").Trim().ToLowerInvariant();
        _followSystem = t is not ("light" or "dark");
        Current = t switch
        {
            "light" => "light",
            "dark" => "dark",
            _ => SystemIsLight() ? "light" : "dark"
        };
        ApplyInternal();
    }

    /// <summary>跟随系统模式下系统主题变化时调用。</summary>
    public static void ReapplyIfSystem()
    {
        var app = Application.Current;
        if (app is null)
            return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(ReapplyIfSystem);
            return;
        }

        if (!_followSystem)
            return;

        var effective = SystemIsLight() ? "light" : "dark";
        if (effective == Current)
            return;

        Current = effective;
        ApplyInternal();
    }

    private static void ApplyInternal()
    {
        try
        {
            ApplicationThemeManager.Apply(Current == "light" ? ApplicationTheme.Light : ApplicationTheme.Dark);
        }
        catch
        {
            // WPF-UI 主题应用失败时不影响自研 token
        }

        var app = Application.Current;
        if (app is null)
            return;

        var uri = new Uri($"Themes/Tokens.{(Current == "light" ? "Light" : "Dark")}.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };
        if (_tokens is not null)
            app.Resources.MergedDictionaries.Remove(_tokens);
        app.Resources.MergedDictionaries.Insert(0, dict);
        _tokens = dict;

        ApplyAccent(app);

        ThemeChanged?.Invoke();
    }

    private static void ApplyAccent(Application app)
    {
        if (_accentDict is not null)
        {
            app.Resources.MergedDictionaries.Remove(_accentDict);
            _accentDict = null;
        }

        if (string.IsNullOrWhiteSpace(_accent))
            return;

        Color color;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(_accent)!;
        }
        catch
        {
            return;
        }

        // 覆盖自研 token（追加在最后，优先级最高）
        _accentDict = new ResourceDictionary
        {
            ["AppAccentBrush"] = new SolidColorBrush(color),
            ["AppSelectionBrush"] = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B))
        };
        app.Resources.MergedDictionaries.Add(_accentDict);

        // 尝试同步 WPF-UI 控件强调色（不可用则忽略）
        try
        {
            ApplicationAccentColorManager.Apply(color, Current == "light" ? ApplicationTheme.Light : ApplicationTheme.Dark, false);
        }
        catch
        {
            try { ApplicationAccentColorManager.Apply(color); } catch { /* 忽略 */ }
        }
    }

    private static bool SystemIsLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i != 0;
        }
        catch
        {
            return false;
        }
    }
}
