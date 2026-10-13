using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MenuItem = System.Windows.Controls.MenuItem;
using Button = System.Windows.Controls.Button;

namespace LitSSHmcp.App.Views;

/// <summary>
/// 内嵌远程桌面多标签窗口（RDP / VNC）。标签内容用独立的 <see cref="RemoteDesktopTabView"/> 实例常驻
/// （放在同一 Grid 内切换可见性，避免 ActiveX / WebView2 因切换标签被销毁重连）；标签可分离为独立窗口 / 收回。
/// </summary>
public partial class RemoteDesktopSessionWindow : FluentWindow
{
    private readonly IConfigService _configService;
    private readonly RemoteDesktopSessionWindowViewModel _vm = new();
    private readonly Dictionary<RemoteDesktopSessionViewModel, RemoteDesktopTabView> _views = new();
    private readonly Dictionary<RemoteDesktopSessionViewModel, RemoteDesktopFloatWindow> _floatWindows = new();
    private bool _allowClose;

    public RemoteDesktopSessionWindow(IConfigService configService)
    {
        InitializeComponent();
        _configService = configService;
        DataContext = _vm;
        WindowLayout.Attach(this, "remote-desktop-session");
        _vm.PropertyChanged += OnVmPropertyChanged;
        Closed += (_, _) => CloseAll();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemoteDesktopSessionWindowViewModel.SelectedSession))
            ApplySelection();
    }

    /// <summary>打开或激活某个远程桌面的标签页。</summary>
    public void AddOrActivateSession(RemoteDesktopConfig config, SshServerConfig? tunnelServer)
    {
        var existing = _vm.Sessions.FirstOrDefault(s => s.Config.Id == config.Id);
        if (existing is not null)
        {
            if (_floatWindows.TryGetValue(existing, out var fw) && fw.IsLoaded)
                fw.Activate();
            else
                Select(existing);
            return;
        }

        var sessionVm = new RemoteDesktopSessionViewModel(config, tunnelServer);
        var view = new RemoteDesktopTabView(sessionVm) { Visibility = Visibility.Collapsed };
        _views[sessionVm] = view;
        ContentHost.Children.Add(view);
        _vm.Sessions.Add(sessionVm);
        _vm.DockedSessions.Add(sessionVm);
        _vm.RaiseCounts();
        Select(sessionVm);
    }

    private void Select(RemoteDesktopSessionViewModel? sessionVm)
    {
        _vm.SelectedSession = sessionVm;   // 触发 ApplySelection
    }

    private void ApplySelection()
    {
        foreach (var s in _vm.Sessions)
            s.IsSelected = ReferenceEquals(s, _vm.SelectedSession);

        foreach (var kv in _views)
        {
            if (_floatWindows.ContainsKey(kv.Key))
                continue;   // 已分离：不参与主窗口内容切换
            kv.Value.Visibility = ReferenceEquals(kv.Key, _vm.SelectedSession) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---- 分离 / 收回 ----

    private void Float(RemoteDesktopSessionViewModel sessionVm)
    {
        if (_floatWindows.TryGetValue(sessionVm, out var existing) && existing.IsLoaded)
        {
            existing.Activate();
            return;
        }

        // 交出隧道（不释放），独立窗口只重建查看器控件 —— 重连最短
        (string Host, int Port, string? ForwardId)? tunnel = null;
        if (_views.Remove(sessionVm, out var view))
        {
            tunnel = view.DetachForMove();
            ContentHost.Children.Remove(view);
        }
        _vm.DockedSessions.Remove(sessionVm);
        _vm.RaiseCounts();

        var win = tunnel is { } t
            ? new RemoteDesktopFloatWindow(sessionVm, t.Host, t.Port, t.ForwardId) { Topmost = false }
            : new RemoteDesktopFloatWindow(sessionVm) { Topmost = false };
        win.DockRequested += Dock;
        win.CloseRequested += Close;
        _floatWindows[sessionVm] = win;
        win.Show();

        if (ReferenceEquals(_vm.SelectedSession, sessionVm))
            Select(_vm.DockedSessions.FirstOrDefault());
    }

    private void Dock(RemoteDesktopSessionViewModel sessionVm)
    {
        RemoteDesktopTabView view;
        if (_floatWindows.Remove(sessionVm, out var win))
        {
            var tunnel = win.TakeTunnelForDock();   // 交出隧道（不释放）
            win.Close();
            view = tunnel is { } t
                ? new RemoteDesktopTabView(sessionVm, t.Host, t.Port, t.ForwardId)
                : new RemoteDesktopTabView(sessionVm);
        }
        else
        {
            view = new RemoteDesktopTabView(sessionVm);
        }

        view.Visibility = Visibility.Collapsed;
        _views[sessionVm] = view;
        ContentHost.Children.Add(view);

        if (!_vm.DockedSessions.Contains(sessionVm))
            _vm.DockedSessions.Add(sessionVm);
        _vm.RaiseCounts();
        Select(sessionVm);
    }

    // ---- 关闭 ----

    private void Close(RemoteDesktopSessionViewModel sessionVm)
    {
        if (MessageBox.Show(this, $"确定关闭远程桌面「{sessionVm.Title}」？该连接将断开。",
                "关闭标签", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        if (_floatWindows.Remove(sessionVm, out var win))
        {
            win.SuppressDock();
            win.Close();
        }

        if (_views.Remove(sessionVm, out var view))
        {
            view.CloseSession();
            ContentHost.Children.Remove(view);
        }

        _vm.Sessions.Remove(sessionVm);
        _vm.DockedSessions.Remove(sessionVm);
        _vm.RaiseCounts();
        if (ReferenceEquals(_vm.SelectedSession, sessionVm))
            Select(_vm.DockedSessions.FirstOrDefault());
    }

    private void CloseAll()
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        foreach (var win in _floatWindows.Values)
        {
            win.SuppressDock();
            win.Close();
        }
        _floatWindows.Clear();

        foreach (var view in _views.Values)
            view.CloseSession();
        _views.Clear();
        ContentHost.Children.Clear();
        _vm.Sessions.Clear();
        _vm.DockedSessions.Clear();
        _vm.RaiseCounts();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose && _vm.Sessions.Count > 0)
        {
            var result = MessageBox.Show(this,
                $"确定关闭「远程桌面」窗口？当前 {_vm.Sessions.Count} 个连接将断开。",
                "关闭", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _allowClose = true;
        }
        base.OnClosing(e);
    }

    // ---- 自绘顶部栏 ----

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
            return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* ignore */ }
        }
    }

    private void OnMinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeWindow(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ---- 工具栏 / 标签 ----

    private async void OnNewConnection(object sender, RoutedEventArgs e)
    {
        var config = await _configService.LoadConfigAsync();
        if (config.RemoteDesktops.Length == 0)
        {
            MessageBox.Show("尚未配置任何远程桌面连接。请先到「远程桌面」管理页添加。",
                "远程桌面", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var menu = new ContextMenu();
        foreach (var rd in config.RemoteDesktops)
        {
            var captured = rd;
            var withTunnel = !string.IsNullOrWhiteSpace(captured.TunnelServerId)
                ? $"（经 {config.Servers.FirstOrDefault(s => s.Id == captured.TunnelServerId)?.Name ?? "?"}）"
                : string.Empty;
            var item = new MenuItem { Header = $"{captured.ProtocolText} · {captured.DisplayName}{withTunnel}" };
            item.Click += (_, _) =>
            {
                var tunnel = string.IsNullOrWhiteSpace(captured.TunnelServerId)
                    ? null
                    : config.Servers.FirstOrDefault(s => s.Id == captured.TunnelServerId);
                AddOrActivateSession(captured, tunnel);
            };
            menu.Items.Add(item);
        }

        if (sender is FrameworkElement fe)
        {
            menu.PlacementTarget = fe;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        }
        menu.IsOpen = true;
    }

    private async void OnExternalOpen(object sender, RoutedEventArgs e)
    {
        var sessionVm = _vm.SelectedSession;
        if (sessionVm is null)
        {
            MessageBox.Show("请先选择一个标签。", "远程桌面", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (ok, message) = await AppServiceFactory.RemoteDesktopService.ConnectAsync(sessionVm.Config, sessionVm.TunnelServer);
        sessionVm.Status = message;
        if (!ok)
            MessageBox.Show(message, "远程桌面", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedItem is RemoteDesktopSessionViewModel vm)
            _vm.SelectedSession = vm;
    }

    private void OnDetachItem(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RemoteDesktopSessionViewModel sessionVm)
            Float(sessionVm);
    }

    // ---- 标签拖出分离（与「SSH 会话管理」一致） ----

    private const string TabDragFormat = "LitSshRdpTab";
    private Point _dragStart;
    private RemoteDesktopSessionViewModel? _dragItem;
    private bool _dragOutside;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private void OnTabsPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = null;
        var tab = FindAncestor<TabItem>(e.OriginalSource as DependencyObject);
        if (tab is null || FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
            return;
        _dragStart = e.GetPosition(null);
        _dragItem = tab.DataContext as RemoteDesktopSessionViewModel;
    }

    private void OnTabsPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _dragItem;
        _dragItem = null;
        if (item is null)
            return;

        _dragOutside = false;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(TabDragFormat, item), DragDropEffects.Move);
        if (_dragOutside)
            Float(item);
        _dragOutside = false;
    }

    /// <summary>拖拽中用屏幕坐标判断光标是否离开窗口范围（离开=拖出，松开即分离为独立窗口）。</summary>
    private void OnDragQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed)
        {
            _dragOutside = false;
            return;
        }
        if ((e.KeyStates & DragDropKeyStates.LeftMouseButton) == 0)
            return;

        try
        {
            if (!GetCursorPos(out var pt))
                return;
            var topLeft = PointToScreen(new Point(0, 0));
            var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
            _dragOutside = pt.X < topLeft.X || pt.Y < topLeft.Y || pt.X > bottomRight.X || pt.Y > bottomRight.Y;
        }
        catch
        {
            // 忽略
        }
    }

    private void OnTabClose(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is RemoteDesktopSessionViewModel sessionVm)
            Close(sessionVm);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
