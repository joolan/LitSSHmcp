using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace LitSSHmcp.App.Views;

/// <summary>SSH 会话管理：多标签窗口（每个服务器一个标签），非模态、不置顶。</summary>
public partial class SshSessionWindow : FluentWindow
{
    public SshSessionWindow()
    {
        InitializeComponent();
        WindowLayout.Attach(this, "ssh-session");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel { HasSessions: true } vm)
        {
            var result = System.Windows.MessageBox.Show(
                $"确定关闭 SSH 会话管理？当前 {vm.Sessions.Count} 个会话将断开。",
                "关闭", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (result != System.Windows.MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        base.OnClosing(e);
    }

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

    // ---- 布局切换（标签 / 平铺） ----

    private void OnLayoutTabs(object sender, RoutedEventArgs e) => SetLayout(SessionLayout.Tabs);
    private void OnLayoutTile1(object sender, RoutedEventArgs e) => SetLayout(SessionLayout.Tile1);
    private void OnLayoutTile2(object sender, RoutedEventArgs e) => SetLayout(SessionLayout.Tile2);

    private void SetLayout(SessionLayout layout)
    {
        if (DataContext is MainViewModel vm)
            vm.SshLayout = layout;
    }

    // ---- 新建连接（空状态入口 + 标签右侧「+」） ----

    private void OnNewConnection(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        var menu = new System.Windows.Controls.ContextMenu();
        var servers = vm.Servers.Where(s => !s.Disabled).ToList();
        if (servers.Count == 0)
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "（无可用服务器）", IsEnabled = false });
        }
        else
        {
            foreach (var server in servers)
            {
                var sub = new System.Windows.Controls.MenuItem { Header = server.Name };
                var command = new System.Windows.Controls.MenuItem { Header = "命令会话" };
                command.Click += (_, _) => vm.Connect(server);
                var terminal = new System.Windows.Controls.MenuItem { Header = "交互式终端" };
                terminal.Click += (_, _) => vm.ConnectTerminal(server);
                sub.Items.Add(command);
                sub.Items.Add(terminal);
                menu.Items.Add(sub);
            }
        }

        menu.PlacementTarget = sender as UIElement;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---- 标签拖拽排序 / 双击重命名 ----

    private const string TabDragFormat = "LitSshTab";
    private Point _tabDragStart;
    private object? _tabDragItem;

    private void OnTabsPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragItem = null;
        var tab = FindAncestor<TabItem>(e.OriginalSource as DependencyObject);
        if (tab is null || FindAncestor<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject) is not null)
            return;
        _tabDragStart = e.GetPosition(null);
        _tabDragItem = tab.DataContext;
    }

    private void OnTabsPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_tabDragItem is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _tabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _tabDragItem;
        _tabDragItem = null;
        var data = new DataObject(TabDragFormat, item);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
    }

    private void OnTabsDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(TabDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTabsDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(TabDragFormat) || DataContext is not MainViewModel vm)
            return;
        var source = e.Data.GetData(TabDragFormat);
        var target = FindAncestor<TabItem>(e.OriginalSource as DependencyObject)?.DataContext;
        if (source is null || target is null || ReferenceEquals(source, target))
            return;

        var from = vm.Sessions.IndexOf(source);
        var to = vm.Sessions.IndexOf(target);
        if (from >= 0 && to >= 0)
            vm.Sessions.Move(from, to);
        e.Handled = true;
    }

    private void OnTabsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var tab = FindAncestor<TabItem>(e.OriginalSource as DependencyObject);
        if (tab is null || FindAncestor<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject) is not null)
            return;
        if (tab.DataContext is not ISshTab sshTab)
            return;

        var name = TextInputDialog.Prompt(this, "重命名标签", "新名称:", sshTab.Title);
        if (!string.IsNullOrWhiteSpace(name))
            sshTab.Rename(name!);
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
