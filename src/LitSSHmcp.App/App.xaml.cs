using System.IO;
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

        // 兜底：任何未处理异常都不应让整个 APP 直接退出（例如终端断开时的边界情况）
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => SafeLog(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { SafeLog(args.Exception); args.SetObserved(); };

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

        // 文件/文件夹同步调度器：仅 App 运行期间执行
        try
        {
            _syncScheduler = new SyncScheduler(new ConfigService());
            _syncScheduler.Start();
            Exit += (_, _) => _syncScheduler?.Dispose();
        }
        catch
        {
            // 调度器启动失败不影响 App
        }

        // 端口转发：退出时统一停止活动隧道并断开 SSH 连接
        Exit += (_, _) => AppServiceFactory.DisposePortForwardService();
    }

    private static SyncScheduler? _syncScheduler;

    private static readonly HashSet<string> _shownErrors = new();

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        SafeLog(e.Exception);
        e.Handled = true;

        // 同一条错误只提示一次，避免定时/绑定类异常反复弹窗被误认为卡死
        var key = e.Exception.GetType().Name + ": " + e.Exception.Message;
        bool show;
        lock (_shownErrors)
        {
            show = _shownErrors.Add(key);
            if (_shownErrors.Count > 200)
                _shownErrors.Clear();
        }

        if (!show)
            return;

        try
        {
            MessageBox.Show("发生未处理错误（已拦截，程序继续运行）：\n\n" + e.Exception.Message,
                "LitSSH", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // 忽略弹窗失败
        }
    }

    private static void SafeLog(Exception? ex)
    {
        if (ex is null)
            return;
        try
        {
            var dir = ConfigPaths.LogsDir;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app-errors.log"),
                $"[{DateTime.Now:O}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 记录日志失败不影响运行
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
