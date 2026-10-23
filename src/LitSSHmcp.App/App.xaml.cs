using System.IO;
using System.Linq;
using System.Threading;
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
        ConfigConflictNotifier.ConflictDetected += OnConfigConflict;

        // 异步读取配置并应用主题：**不能**在 UI 线程同步等待（GetAwaiter().GetResult() 会死锁，
        // 导致 OnStartup 不返回、主窗口永不创建——表现为"双击没反应"）。
        _ = ApplyThemeAsync();

        // 启动门卫：未配置应用锁先弹「设置密码」窗口（可「暂不启用」跳过）；
        // 已配置则必须解锁（密码或 Windows Hello）后才创建主窗口——未解锁不实例化任何会话/凭据 UI。
        _ = ShowMainWindowThroughLockAsync();

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

            // 触发事件调度器：定时 + 订阅同步任务完成事件做联动；仅 App 运行期间执行
            _triggerScheduler = new TriggerScheduler(new ConfigService());
            _syncScheduler.TaskCompleted += _triggerScheduler.OnSyncCompleted;
            _triggerScheduler.Start();

            Exit += (_, _) =>
            {
                _triggerScheduler?.Dispose();
                _syncScheduler?.Dispose();
            };
        }
        catch
        {
            // 调度器启动失败不影响 App
        }

        // 端口转发：退出时统一停止活动隧道并断开 SSH 连接
        Exit += (_, _) => AppServiceFactory.DisposePortForwardService();

        // 远程桌面：退出时拆除活动隧道（查看器进程由用户自行关闭）
        Exit += (_, _) => AppServiceFactory.DisposeRemoteDesktopService();
    }

    private static SyncScheduler? _syncScheduler;
    private static TriggerScheduler? _triggerScheduler;

    /// <summary>
    /// 启动门卫：未配置应用锁先弹「设置密码」窗口（可「暂不启用」跳过，下次启动仍会提示）；
    /// 已配置则必须解锁后才创建主窗口。直接关闭锁窗口 = 退出程序。
    /// </summary>
    private async Task ShowMainWindowThroughLockAsync()
    {
        // 门卫阶段显式管理退出：OnLastWindowClose 下锁窗口一关（解锁成功/取消）窗口数即为 0，
        // WPF 会直接 Shutdown，主窗口根本来不及创建。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var configService = new ConfigService();
        bool configured;
        try
        {
            var config = await configService.LoadStrictAsync().ConfigureAwait(false);
            configured = Core.Services.Security.AppLockService.IsConfigured(config.AppLock);
        }
        catch (Exception ex)
        {
            // 配置损坏时不能当作“未设置密码”继续进入，否则应用锁被绕过。保持 fail-closed 并退出。
            await Dispatcher.InvokeAsync(() =>
            {
                System.Windows.MessageBox.Show(
                    "无法读取配置文件，为避免绕过应用锁，程序将退出。\n\n" + ex.Message +
                    "\n\n请修复或从备份恢复配置后重试。",
                    "LitSSH", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            });
            return;
        }

        var tcs = new TaskCompletionSource<bool>();
        await Dispatcher.InvokeAsync(() =>
        {
            var mode = configured
                ? Views.AppLockWindowMode.Unlock
                : Views.AppLockWindowMode.SetPassword;
            var lockWindow = new Views.AppLockWindow(new Core.Services.Security.AppLockService(configService), mode, relock: false);
            lockWindow.Unlocked += (_, _) => tcs.TrySetResult(true);    // 解锁 / 设置成功 → 进入
            lockWindow.Skipped += (_, _) => tcs.TrySetResult(false);    // 暂不启用 → 也进入（保持未锁定）
            lockWindow.Closed += (_, _) => tcs.TrySetCanceled();        // 直接关闭 → 退出
            lockWindow.Show();
        });

        try
        {
            await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            Shutdown();
            return;
        }

        await Dispatcher.InvokeAsync(CreateMainWindow);
    }

    private void CreateMainWindow()
    {
        // 主窗口真正关闭（「退出程序」）即连同所有独立窗口一起退出；
        // 托盘「最小化到托盘」走 Closing 取消，不触发。
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
        AppLockMonitor.Attach(this, mainWindow);   // 跟随 Windows 锁屏：回来时重新上锁
    }

    private static readonly HashSet<string> _shownErrors = new();

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        ReportError(e.Exception, "未处理异常");
        e.Handled = true;
    }

    /// <summary>
    /// 统一错误日志/去重提示（P3-3）：Dispatcher 未处理异常与 AsyncOperation 共用。
    /// 不抛出、不阻塞调用线程。
    /// </summary>
    internal static void ReportError(Exception? ex, string? operation = null)
    {
        if (ex is null)
            return;

        SafeLog(ex);

        // 同一条错误只提示一次，避免定时/绑定类异常反复弹窗被误认为卡死
        var key = (operation ?? "error") + "|" + ex.GetType().Name + ": " + ex.Message;
        bool show;
        lock (_shownErrors)
        {
            show = _shownErrors.Add(key);
            if (_shownErrors.Count > 200)
                _shownErrors.Clear();
        }

        if (!show)
            return;

        void Show()
        {
            try
            {
                var prefix = string.IsNullOrWhiteSpace(operation)
                    ? "发生未处理错误（已拦截，程序继续运行）"
                    : $"“{operation}”执行失败（已拦截，程序继续运行）";
                MessageBox.Show(prefix + "：\n\n" + ex.Message,
                    "LitSSH", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch
            {
                // 忽略弹窗失败
            }
        }

        var dispatcher = Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;
        if (dispatcher.CheckAccess())
            Show();
        else
            dispatcher.BeginInvoke(new Action(Show));
    }

    private static int _configConflictPrompting;

    /// <summary>同一时刻只弹一个配置冲突提示；提示后由用户关闭/重开窗口加载最新配置。</summary>
    private static void OnConfigConflict(ConfigConflictException ex)
    {
        if (Interlocked.Exchange(ref _configConflictPrompting, 1) == 1)
            return;

        var dispatcher = Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            Volatile.Write(ref _configConflictPrompting, 0);
            return;
        }

        void Show()
        {
            try
            {
                var paths = ex.ConflictPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToArray();
                var detail = paths.Length == 0
                    ? "（未提供具体字段）"
                    : string.Join("\n", paths.Take(10)) +
                      (paths.Length > 10 ? $"\n… 另 {paths.Length - 10} 项" : string.Empty);
                new UserDialogService().Warn(
                    $"配置已被其他窗口或进程修改，以下字段存在冲突：\n\n{detail}\n\n" +
                    "请关闭并重新打开当前窗口（或重新加载配置）后再保存，避免覆盖他人的修改。",
                    "配置冲突");
            }
            catch
            {
                // 弹窗失败不掩盖原始保存结果是调用方自身提示的职责
            }
            finally
            {
                Volatile.Write(ref _configConflictPrompting, 0);
            }
        }

        if (dispatcher.CheckAccess())
            Show();
        else
            dispatcher.BeginInvoke(new Action(Show));
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
