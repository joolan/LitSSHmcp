using System.Windows;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Win32;

namespace LitSSHmcp.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 异步读取配置并应用主题：**不能**在 UI 线程同步等待（GetAwaiter().GetResult() 会死锁，
        // 导致 OnStartup 不返回、StartupUri 主窗口永不创建——表现为"双击没反应"）。
        _ = ApplyThemeAsync();

        // 跟随系统模式下，系统主题变化时自动重应用
        try
        {
            SystemEvents.UserPreferenceChanged += (_, _) => ThemeService.ReapplyIfSystem();
        }
        catch
        {
            // 非关键路径
        }
    }

    private static async Task ApplyThemeAsync()
    {
        string theme;
        string? accent;
        try
        {
            var config = await new ConfigService().LoadConfigAsync();
            theme = config.Ui?.Theme ?? "system";
            accent = config.Ui?.Accent;
        }
        catch
        {
            theme = "system";
            accent = null;
        }

        try
        {
            ThemeService.Apply(theme, accent);
        }
        catch
        {
            // 主题应用失败不影响启动
        }
    }
}
