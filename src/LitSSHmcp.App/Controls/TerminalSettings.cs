using System.IO;
using System.Text.Json;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.Controls;

/// <summary>终端配色主题（前景/背景/光标/选中/查找/ANSI 16 色）。</summary>
public sealed record TerminalTheme(string Name, uint Fg, uint Bg, uint Cursor,
    uint SelectionBg = 0x264F78, uint FindBg = 0x6B5D00, uint FindCurrentBg = 0xD7BA5A)
{
    public static readonly uint[] DefaultAnsi16 =
    {
        0x000000, 0xCD0000, 0x00CD00, 0xCDCD00, 0x0000EE, 0xCD00CD, 0x00CDCD, 0xE5E5E5,
        0x7F7F7F, 0xFF0000, 0x00FF00, 0xFFFF00, 0x5C5CFF, 0xFF00FF, 0x00FFFF, 0xFFFFFF
    };

    public uint[] Ansi16Palette { get; init; } = DefaultAnsi16;
}

/// <summary>终端外观偏好（全局），持久化到 %APPDATA%\LitSSH\ui-prefs.json。</summary>
public static class TerminalSettings
{
    public static readonly TerminalTheme[] Themes =
    {
        new("深色", 0xD4D4D4, 0x1E1E1E, 0xD4D4D4),
        new("纯黑", 0xFFFFFF, 0x000000, 0xFFFFFF),
        new("Solarized Dark", 0x839496, 0x002B36, 0x93A1A1, 0x0F3A46)
        {
            Ansi16Palette = new uint[]
            {
                0x073642, 0xDC322F, 0x859900, 0xB58900, 0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5,
                0x002B36, 0xCB4B16, 0x586E75, 0x657B83, 0x839496, 0x6C71C4, 0x93A1A1, 0xFDF6E3
            }
        },
        new("Dracula", 0xF8F8F2, 0x282A36, 0xF8F8F2, 0x44475A)
        {
            Ansi16Palette = new uint[]
            {
                0x21222C, 0xFF5555, 0x50FA7B, 0xF1FA8C, 0xBD93F9, 0xFF79C6, 0x8BE9FD, 0xF8F8F2,
                0x6272A4, 0xFF6E6E, 0x69FF94, 0xFFFFA5, 0xD6ACFF, 0xFF92DF, 0xA4FFFF, 0xFFFFFF
            }
        },
        new("浅色", 0x333333, 0xFFFFFF, 0x333333, 0xB3D7FF, 0xFFF2A8, 0xFFD54F)
    };

    public static string FontFamilyName { get; private set; } = "Consolas";
    public static double FontSize { get; private set; } = 14;
    public static int Scrollback { get; private set; } = 2000;
    public static string CursorStyle { get; private set; } = "block"; // block / bar / underline
    public static bool CopyOnSelect { get; private set; } = true;
    public static bool RecordSessions { get; private set; } = true;
    public static int LogRetentionDays { get; private set; } = 30;
    public static bool ShowFileBrowser { get; private set; } = true;
    private static TerminalTheme? _customTheme;
    public static IReadOnlyList<TerminalTheme> AllThemes => _customTheme is null
        ? Themes
        : Themes.Append(_customTheme).ToArray();
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
        RecordSessions = UiPrefs.GetString("terminal.recordSessions", "true") == "true";
        LogRetentionDays = (int)Math.Clamp(UiPrefs.GetDouble("terminal.logRetentionDays", 30), 1, 3650);
        ShowFileBrowser = UiPrefs.GetString("terminal.showFileBrowser", "true") == "true";
        var customJson = UiPrefs.GetString("terminal.customTheme", string.Empty);
        _customTheme = null;
        if (!string.IsNullOrWhiteSpace(customJson))
        {
            try { _customTheme = JsonSerializer.Deserialize<TerminalTheme>(customJson); }
            catch { _customTheme = null; }
        }
        var themeName = UiPrefs.GetString("terminal.theme", Themes[0].Name);
        Theme = AllThemes.FirstOrDefault(t => string.Equals(t.Name, themeName, StringComparison.Ordinal)) ?? Themes[0];
    }

    public static TerminalTheme? ImportWindowsTerminalTheme(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.TryGetProperty("schemes", out var schemes) && schemes.ValueKind == JsonValueKind.Array &&
            schemes.GetArrayLength() > 0)
            root = schemes[0];
        var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() : "Windows Terminal";
        name = string.IsNullOrWhiteSpace(name) ? "Windows Terminal" : name.Trim();
        if (Themes.Any(t => string.Equals(t.Name, name, StringComparison.Ordinal)))
            name += " (导入)";
        var fg = ReadColor(root, "foreground", 0xD4D4D4);
        var bg = ReadColor(root, "background", 0x1E1E1E);
        var cursor = ReadColor(root, "cursorColor", fg);
        var selection = ReadColor(root, "selectionBackground", 0x264F78);
        var ansi = TerminalTheme.DefaultAnsi16.ToArray();
        var names = new[] { "black", "red", "green", "yellow", "blue", "purple", "cyan", "white",
            "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightPurple", "brightCyan", "brightWhite" };
        for (var i = 0; i < names.Length; i++) ansi[i] = ReadColor(root, names[i], ansi[i]);
        var theme = new TerminalTheme(name ?? "Windows Terminal", fg, bg, cursor,
            selection, 0x6B5D00, 0xD7BA5A) { Ansi16Palette = ansi };
        _customTheme = theme;
        UiPrefs.SetString("terminal.customTheme", JsonSerializer.Serialize(theme));
        UiPrefs.SetString("terminal.theme", theme.Name);
        Load();
        Changed?.Invoke();
        return theme;
    }

    private static uint ReadColor(JsonElement root, string name, uint fallback)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return fallback;
        var text = (value.GetString() ?? string.Empty).Trim().TrimStart('#');
        if (text.Length == 8) text = text[2..];
        return text.Length == 6 && uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var color) ? color : fallback;
    }

    public static void Save(string fontFamily, double fontSize, int scrollback, string cursorStyle, bool copyOnSelect, string themeName, bool recordSessions, bool showFileBrowser, int logRetentionDays = 30)
    {
        UiPrefs.SetString("terminal.fontFamily", string.IsNullOrWhiteSpace(fontFamily) ? "Consolas" : fontFamily);
        UiPrefs.SetDouble("terminal.fontSize", Math.Clamp(fontSize, 8, 32));
        UiPrefs.SetDouble("terminal.scrollback", Math.Clamp(scrollback, 200, 100000));
        UiPrefs.SetString("terminal.cursor", cursorStyle);
        UiPrefs.SetString("terminal.copyOnSelect", copyOnSelect ? "true" : "false");
        UiPrefs.SetString("terminal.recordSessions", recordSessions ? "true" : "false");
        UiPrefs.SetString("terminal.showFileBrowser", showFileBrowser ? "true" : "false");
        UiPrefs.SetDouble("terminal.logRetentionDays", Math.Clamp(logRetentionDays, 1, 3650));
        UiPrefs.SetString("terminal.theme", themeName);
        Load();
        Changed?.Invoke();
    }
}
