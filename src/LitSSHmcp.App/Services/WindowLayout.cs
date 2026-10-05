using System.IO;
using System.Text.Json;
using System.Windows;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services;

/// <summary>窗口尺寸/位置记忆：按 key 保存到 %APPDATA%\LitSSH\ui-layout.json，重开还原。</summary>
public static class WindowLayout
{
    private sealed class Entry
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public string? State { get; set; }
    }

    private static readonly string FilePath = Path.Combine(ConfigPaths.AppDataDir, "ui-layout.json");
    private static readonly object Gate = new();

    public static void Attach(Window window, string key)
    {
        try
        {
            window.SourceInitialized += (_, _) =>
            {
                var e = Load().GetValueOrDefault(key);
                if (e is null)
                    return;
                if (e.Width >= window.MinWidth && e.Height >= window.MinHeight)
                {
                    window.Width = e.Width;
                    window.Height = e.Height;
                }
                if (IsOnScreen(e.Left, e.Top, window.Width, window.Height))
                {
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = e.Left;
                    window.Top = e.Top;
                }
                if (string.Equals(e.State, "Maximized", StringComparison.Ordinal))
                    window.WindowState = WindowState.Maximized;
            };

            window.Closing += (_, _) => Save(key, window);
        }
        catch
        {
            // 布局记忆失败不影响功能
        }
    }

    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        var vLeft = SystemParameters.VirtualScreenLeft;
        var vTop = SystemParameters.VirtualScreenTop;
        var vRight = vLeft + SystemParameters.VirtualScreenWidth;
        var vBottom = vTop + SystemParameters.VirtualScreenHeight;
        return left + width > vLeft + 40 && left < vRight - 40 && top + 30 > vTop && top < vBottom - 30;
    }

    private static Dictionary<string, Entry> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new Dictionary<string, Entry>();
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(json) ?? new Dictionary<string, Entry>();
            }
            catch
            {
                return new Dictionary<string, Entry>();
            }
        }
    }

    private static void Save(string key, Window window)
    {
        lock (Gate)
        {
            try
            {
                var map = Load();
                var restored = window.RestoreBounds;
                map[key] = new Entry
                {
                    Width = window.Width,
                    Height = window.Height,
                    Left = restored.Left,
                    Top = restored.Top,
                    State = window.WindowState == WindowState.Maximized ? "Maximized" : "Normal"
                };
                Directory.CreateDirectory(ConfigPaths.AppDataDir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // 忽略
            }
        }
    }
}
