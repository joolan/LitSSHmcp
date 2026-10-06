using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.Controls;

/// <summary>终端配色主题（前景/背景/光标）。</summary>
public sealed record TerminalTheme(string Name, uint Fg, uint Bg, uint Cursor);

/// <summary>终端外观偏好（全局），持久化到 %APPDATA%\LitSSH\ui-prefs.json。</summary>
public static class TerminalSettings
{
    public static readonly TerminalTheme[] Themes =
    {
        new("深色", 0xD4D4D4, 0x1E1E1E, 0xD4D4D4),
        new("纯黑", 0xFFFFFF, 0x000000, 0xFFFFFF),
        new("Solarized Dark", 0x839496, 0x002B36, 0x93A1A1),
        new("Dracula", 0xF8F8F2, 0x282A36, 0xF8F8F2),
        new("浅色", 0x333333, 0xFFFFFF, 0x333333)
    };

    public static string FontFamilyName { get; private set; } = "Consolas";
    public static double FontSize { get; private set; } = 14;
    public static int Scrollback { get; private set; } = 2000;
    public static string CursorStyle { get; private set; } = "block"; // block / bar / underline
    public static bool CopyOnSelect { get; private set; } = true;
    public static TerminalTheme Theme { get; private set; } = Themes[0];

    public static event Action? Changed;

    static TerminalSettings() => Load();

    public static void Load()
    {
        FontFamilyName = UiPrefs.GetString("terminal.fontFamily", "Consolas");
        FontSize = UiPrefs.GetDouble("terminal.fontSize", 14);
        Scrollback = (int)UiPrefs.GetDouble("terminal.scrollback", 2000);
        CursorStyle = UiPrefs.GetString("terminal.cursor", "block");
        CopyOnSelect = UiPrefs.GetString("terminal.copyOnSelect", "true") == "true";
        var themeName = UiPrefs.GetString("terminal.theme", Themes[0].Name);
        Theme = Array.Find(Themes, t => t.Name == themeName) ?? Themes[0];
    }

    public static void Save(string fontFamily, double fontSize, int scrollback, string cursorStyle, bool copyOnSelect, string themeName)
    {
        UiPrefs.SetString("terminal.fontFamily", string.IsNullOrWhiteSpace(fontFamily) ? "Consolas" : fontFamily);
        UiPrefs.SetDouble("terminal.fontSize", Math.Clamp(fontSize, 8, 32));
        UiPrefs.SetDouble("terminal.scrollback", Math.Clamp(scrollback, 200, 100000));
        UiPrefs.SetString("terminal.cursor", cursorStyle);
        UiPrefs.SetString("terminal.copyOnSelect", copyOnSelect ? "true" : "false");
        UiPrefs.SetString("terminal.theme", themeName);
        Load();
        Changed?.Invoke();
    }
}
