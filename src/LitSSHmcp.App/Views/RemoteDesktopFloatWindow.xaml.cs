using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;

namespace LitSSHmcp.App.Views;

/// <summary>
/// 从「远程桌面」窗口分离出来的独立会话窗口：自己新建一个 <see cref="RemoteDesktopTabView"/>；
/// 若主窗口交来隧道句柄（<paramref name="forwardId"/>）则复用该隧道、仅重建查看器控件（重连最短）。
/// </summary>
public partial class RemoteDesktopFloatWindow : FluentWindow
{
    private readonly RemoteDesktopSessionViewModel _session;
    private readonly RemoteDesktopTabView _tabView;
    private bool _suppressDock;
    private bool _suppressRelease;

    /// <summary>请求把会话收回主窗口（「收回」按钮或直接关窗）。</summary>
    public event Action<RemoteDesktopSessionViewModel>? DockRequested;

    /// <summary>请求关闭会话（断开连接）。</summary>
    public event Action<RemoteDesktopSessionViewModel>? CloseRequested;

    public RemoteDesktopFloatWindow(RemoteDesktopSessionViewModel session, string? connectHost = null, int connectPort = 0, string? forwardId = null)
    {
        InitializeComponent();
        _session = session;
        DataContext = session;

        _tabView = new RemoteDesktopTabView(session, connectHost, connectPort, forwardId);
        ContentHost.Content = _tabView;

        WindowLayout.Attach(this, "remote-desktop-float-" + session.Config.Id);
        session.PropertyChanged += OnSessionPropertyChanged;
        UpdateTitle();

        Closed += (_, _) => { if (!_suppressRelease) _tabView.CloseSession(); };
    }

    /// <summary>由主窗口调用：标记为“程序主动关闭”，避免再次触发“收回”。</summary>
    public void SuppressDock() => _suppressDock = true;

    /// <summary>收回前调用：标记为程序关闭、抑制释放，并交出隧道句柄（不释放隧道）供主窗口复用。</summary>
    public (string Host, int Port, string? ForwardId)? TakeTunnelForDock()
    {
        _suppressDock = true;
        _suppressRelease = true;
        return _tabView.DetachForMove();
    }

    private void UpdateTitle()
    {
        Title = _session.Title;
        TitleText.Text = _session.Title;
        StatusText.Text = _session.Status;
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RemoteDesktopSessionViewModel.Title) or nameof(RemoteDesktopSessionViewModel.Status))
            UpdateTitle();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 应用级退出时不再触发「收回主窗口」（主窗口也在关闭中）
        if (!_suppressDock && !AppShutdown.IsShuttingDown)
            DockRequested?.Invoke(_session);   // 直接关窗默认收回主窗口

        _session.PropertyChanged -= OnSessionPropertyChanged;
        base.OnClosing(e);
    }

    private void OnDock(object sender, RoutedEventArgs e) => DockRequested?.Invoke(_session);

    private void OnCloseSession(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(_session);

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
            return;
        if (e.ClickCount == 2)
        {
            OnMaximize(sender, e);
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* ignore */ }
        }
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
