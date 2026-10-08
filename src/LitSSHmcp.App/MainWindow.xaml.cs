using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Services;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace LitSSHmcp.App;

public partial class MainWindow : FluentWindow
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        WindowLayout.Attach(this, "main");
        Closing += OnMainClosing;
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

    private void OnServerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 仅在双击到有效的服务器条目时触发；空白区域双击不打开会话
        if (sender is not ListBox listBox)
            return;

        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item == null)
            return;

        listBox.SelectedItem = item.DataContext;
        ViewModel.ConnectCommand.Execute(null);
    }

    private void OnConnectClick(object sender, RoutedEventArgs e) => ViewModel.ConnectCommand.Execute(null);

    private void OnEditServerClick(object sender, RoutedEventArgs e) => ViewModel.EditServerCommand.Execute(null);

    private void OnDeleteServerClick(object sender, RoutedEventArgs e) => ViewModel.DeleteServerCommand.Execute(null);

    private void OnSnapshotRefreshClick(object sender, RoutedEventArgs e) => ViewModel.SnapshotRefreshCommand.Execute(null);

    private void OnSnapshotHistoryClick(object sender, RoutedEventArgs e) => ViewModel.OpenSnapshotHistoryCommand.Execute(null);

    private void OnCommandKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (sender is TextBox { DataContext: SessionViewModel session } &&
            session.ExecuteCommand.CanExecute(null))
        {
            session.ExecuteCommand.Execute(null);
            e.Handled = true;
        }
    }

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

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "LitSSH MCP Manager",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        FocusHomeAsk();
    }

    private void ExitFromTray()
    {
        _allowClose = true;
        DisposeTray();
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
    }
}
