using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;

namespace LitSSHmcp.App.Views;

/// <summary>从 SSH 会话窗口分离出来的独立会话窗口：紧凑、可缩放、可自由摆放；连接状态由会话 VM 持有。</summary>
public partial class SshSessionFloatWindow : FluentWindow
{
    private readonly object _session;
    private bool _suppressDock;

    /// <summary>请求把会话收回主窗口（「收回」按钮或直接关窗）。</summary>
    public event Action<object>? DockRequested;

    /// <summary>请求关闭会话（断开连接）。</summary>
    public event Action<object>? CloseRequested;

    public SshSessionFloatWindow(MainViewModel host, object session)
    {
        InitializeComponent();
        _session = session;

        // DataContext 为主 VM：使共享 DataTemplate 内的 RelativeSource Window 绑定（关闭命令等）可解析
        DataContext = host;
        ContentHost.Content = session;

        var serverId = (session as ISshTab)?.Server.Id ?? "x";
        var suffix = session is TerminalSessionViewModel ? "-term" : "-cmd";
        WindowLayout.Attach(this, "ssh-float-" + serverId + suffix);

        if (session is INotifyPropertyChanged inpc)
            inpc.PropertyChanged += OnSessionPropertyChanged;
        UpdateTitle();
    }

    /// <summary>由主 VM 调用：标记为"程序主动关闭"，避免再次触发"收回"。</summary>
    public void SuppressDock() => _suppressDock = true;

    private void UpdateTitle()
    {
        var title = (_session as ISshTab)?.Title ?? "SSH 会话";
        Title = title;
        TitleText.Text = title;
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ISshTab.Title))
            UpdateTitle();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_suppressDock)
            DockRequested?.Invoke(_session); // 直接关窗（Alt+F4/系统关闭）默认收回主窗口

        if (_session is INotifyPropertyChanged inpc)
            inpc.PropertyChanged -= OnSessionPropertyChanged;

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
