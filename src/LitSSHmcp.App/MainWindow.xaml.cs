using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Views;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace LitSSHmcp.App;

public partial class MainWindow : FluentWindow
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private bool _allowClose;
    private HelpWindow? _helpWindow;

    /// <summary>最小化到托盘时被隐藏的独立窗口快照（恢复时按快照再显示；托盘期间新弹出的窗口不受影响）。</summary>
    private readonly List<Window> _trayHiddenWindows = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        WindowLayout.Attach(this, "main.v2");
        Closing += OnMainClosing;
        Closed += (_, _) => ViewModel.DisposeAgentPage();
        if (Application.Current is { } app)
            app.Exit += (_, _) => DisposeTray();

        // 启动及切回「主页」时，默认聚焦到 AI 快捷提问输入框
        Loaded += (_, _) => FocusHomeAsk();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPage))
                FocusHomeAsk();
        };
    }

    private void OnHomeNavClick(object sender, RoutedEventArgs e) => FocusHomeAsk();

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.K || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        e.Handled = true;
        var palette = new CommandPaletteWindow(ViewModel) { Owner = this };
        palette.ShowDialog();
    }

    private void FocusHomeAsk()
    {
        if (ViewModel.CurrentPage != MainPage.Home)
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            HomeAskBox.Focus();
            Keyboard.Focus(HomeAskBox);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnEditServerClick(object sender, RoutedEventArgs e) => ViewModel.EditServerCommand.Execute(null);

    private void OnDeleteServerClick(object sender, RoutedEventArgs e) => ViewModel.DeleteServerCommand.Execute(null);

    private void OnSnapshotRefreshClick(object sender, RoutedEventArgs e) => ViewModel.SnapshotRefreshCommand.Execute(null);

    private void OnSnapshotHistoryClick(object sender, RoutedEventArgs e) => ViewModel.OpenSnapshotHistoryCommand.Execute(null);

    // 右键时选中鼠标所在项，使右键菜单操作作用于该项
    private void OnListPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item != null)
            listBox.SelectedItem = item.DataContext;
    }

    // 仅在鼠标位于有效服务器条目上时才弹出右键菜单；空白区域不弹出
    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        var element = listBox.InputHitTest(Mouse.GetPosition(listBox)) as DependencyObject;
        if (FindAncestor<ListBoxItem>(element) == null)
            e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
                return match;

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    // ---- 主页看板 / AI 快捷提问 ----

    private void OnHomeServersClick(object sender, MouseButtonEventArgs e) => ViewModel.NavigateServersCommand.Execute(null);

    private void OnHomeDatasourcesClick(object sender, MouseButtonEventArgs e) => ViewModel.NavigateDataSourcesCommand.Execute(null);

    private void OnHomeApplicationsClick(object sender, MouseButtonEventArgs e) => ViewModel.NavigateApplicationsCommand.Execute(null);

    private void OnHomePortForwardClick(object sender, MouseButtonEventArgs e) => ViewModel.OpenPortForwardCommand.Execute(null);

    private void OnHomeRemoteDesktopClick(object sender, MouseButtonEventArgs e) => ViewModel.NavigateRemoteDesktopsCommand.Execute(null);

    private void OnHomeSyncClick(object sender, MouseButtonEventArgs e) => ViewModel.OpenSyncCommand.Execute(null);

    private void OnHomeTriggerClick(object sender, MouseButtonEventArgs e) => ViewModel.OpenTriggerCommand.Execute(null);

    private void OnOpenHelp(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string topic })
            return;

        if (_helpWindow is { IsLoaded: true })
        {
            _helpWindow.SelectTopic(topic);
            _helpWindow.Activate();
            return;
        }

        _helpWindow = new HelpWindow(topic) { Owner = this };
        _helpWindow.Closed += (_, _) => _helpWindow = null;
        _helpWindow.Show();
    }

    private void OnHomeAddImage(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "添加图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|所有文件|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() == true)
            ViewModel.AddHomeAttachmentPaths(dialog.FileNames);
    }

    private void OnHomeAddFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "添加文件",
            Filter = "所有文件|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() == true)
            ViewModel.AddHomeAttachmentPaths(dialog.FileNames);
    }

    private void OnHomeClearAttachments(object sender, RoutedEventArgs e) => ViewModel.ClearHomeAttachments();

    private void OnHomeSend(object sender, RoutedEventArgs e) => ViewModel.SendHomeAsk();

    private void OnHomeAskKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) == 0)
        {
            ViewModel.HomeQuestion = HomeAskBox.Text;
            ViewModel.SendHomeAsk();
            e.Handled = true;
        }
    }

    private void OnHomeMore(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.ContextMenu is { } menu)
        {
            menu.PlacementTarget = element;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    // ---- 关闭主窗口：选择「最小化到托盘」或「退出程序」 ----

    private void OnMainClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            DisposeTray();
            return;
        }

        var choice = Views.CloseChoiceDialog.Show(this);
        switch (choice)
        {
            case Views.CloseChoice.Exit:
                _allowClose = true;
                DisposeTray();
                // 置退出标记后由 OnMainWindowClose 触发 Application.Shutdown：连同所有独立窗口一起退出，
                // 各窗口 OnClosing 见标记后跳过「还有活动会话」等二次确认，避免退出被中断。
                AppShutdown.IsShuttingDown = true;
                return; // 允许关闭
            case Views.CloseChoice.MinimizeToTray:
                e.Cancel = true;
                MinimizeToTray();
                return;
            default:
                e.Cancel = true;
                return;
        }
    }

    private void MinimizeToTray()
    {
        EnsureTray();

        // 快照式：把此刻已打开的独立窗口一并隐藏（AI 助手/会话管理/拓扑/设置等），
        // 恢复时按快照再显示；托盘期间新弹出的窗口（如审批弹窗）不进快照、照常显示。
        _trayHiddenWindows.Clear();
        foreach (Window window in Application.Current.Windows)
        {
            if (ReferenceEquals(window, this) || !window.IsVisible)
                continue;
            _trayHiddenWindows.Add(window);
            try { window.Hide(); } catch { /* 个别窗口隐藏失败不影响整体 */ }
        }

        Hide();
        try
        {
            _tray?.ShowBalloonTip(2500, "LitSSH", "已最小化到托盘，后台继续运行；双击托盘图标可恢复主界面。",
                System.Windows.Forms.ToolTipIcon.Info);
        }
        catch
        {
            // 气泡提示失败忽略
        }
    }

    private void EnsureTray()
    {
        if (_tray is not null)
        {
            _tray.Visible = true;
            return;
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示主界面", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出程序", null, (_, _) => ExitFromTray());

        _trayIcon = CreateTrayIcon();
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "LitSSH MCP Manager",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    private static System.Drawing.Icon CreateTrayIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/LitSSH.ico", UriKind.Absolute));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new System.Drawing.Icon(stream);
            }
        }
        catch
        {
            // 资源加载失败时回退到系统默认图标
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void RestoreFromTray() => AsyncOperation.Run("从托盘恢复", async () =>
    {
        // 主界面本来就可见（仅最小化到任务栏等）：只激活，不解锁也不做窗口恢复
        if (IsVisible)
        {
            Activate();
            return;
        }

        // 已设置应用锁：恢复主界面前先解锁（未设置密码则直接恢复）。
        // 配置读取/解锁失败必须保持隐藏，不能 fail-open 直接恢复主界面。
        var configService = new ConfigService();
        Core.Models.AppConfig config;
        try
        {
            config = await configService.LoadStrictAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this,
                "无法读取配置文件，已保持主界面隐藏以避免绕过应用锁。\n\n" + ex.Message,
                "LitSSH", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        if (AppLockService.IsConfigured(config.AppLock))
        {
            try
            {
                var lockWindow = new Views.AppLockWindow(new AppLockService(configService), Views.AppLockWindowMode.Unlock, relock: true)
                {
                    Owner = this
                };
                var unlocked = false;
                lockWindow.Unlocked += (_, _) => unlocked = true;
                lockWindow.ShowDialog();   // 模态：解锁前不恢复主界面（relock 模式下不可点 X 绕过）
                if (!unlocked)
                    return;   // 未解锁（点「退出程序」时进程已在退出）
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this,
                    "解锁窗口异常，已保持主界面隐藏以避免绕过应用锁。\n\n" + ex.Message,
                    "LitSSH", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }
        }

        // 先按快照恢复先前隐藏的独立窗口（已关闭的跳过），再显示主窗口并置前
        foreach (var window in _trayHiddenWindows)
        {
            try
            {
                if (window.IsLoaded)
                    window.Show();
            }
            catch
            {
                // 窗口已销毁：跳过
            }
        }
        _trayHiddenWindows.Clear();

        Show();
        WindowState = WindowState.Normal;
        Activate();
        FocusHomeAsk();
    });

    private void ExitFromTray()
    {
        _allowClose = true;
        DisposeTray();
        AppShutdown.IsShuttingDown = true;
        Application.Current.Shutdown();
    }

    private void DisposeTray()
    {
        try
        {
            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
        }
        catch
        {
            // 忽略
        }
        _tray = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
