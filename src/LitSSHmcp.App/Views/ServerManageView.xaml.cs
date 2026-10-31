using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.Views;

public partial class ServerManageView : UserControl
{
    private MainViewModel? ViewModel => DataContext as MainViewModel;

    public ServerManageView()
    {
        InitializeComponent();
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnSearchShortcut), true);
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
                Dispatcher.BeginInvoke(new Action(FocusSearch), System.Windows.Threading.DispatcherPriority.Loaded);
        };
        LitSSHmcp.App.Controls.ColumnChooser.Attach(ServerManageList, "server.columns.v3");
    }

    private void OnChooseColumns(object sender, RoutedEventArgs e)
        => LitSSHmcp.App.Controls.ColumnChooser.Show((FrameworkElement)sender, ServerManageList, "server.columns.v3");

    private void OnSearchShortcut(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            FocusSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Header.IsKeyboardFocusWithin)
        {
            ClearSearch();
            e.Handled = true;
        }
    }

    private void OnClearSearch(object sender, RoutedEventArgs e) => ClearSearch();

    private void ClearSearch()
    {
        if (ViewModel is { } vm)
            vm.ServerSearchText = string.Empty;
        FocusSearch();
    }

    private void FocusSearch()
    {
        Header.FocusFirstInput();
        
    }

    private void OnServerCardMore(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not FrameworkElement { DataContext: SshServerConfig server } anchor)
            return;

        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) =>
            {
                vm.SelectedServer = server;
                action();
            };
            menu.Items.Add(item);
        }

        Add("新建 AI 会话…", () => vm.NewAiSessionCommand.Execute(null));
        Add("打开代理浏览器 (SOCKS)…", () => vm.OpenProxiedBrowserCommand.Execute(null), enabled: !server.Disabled);
        Add("远程互传（服务器↔服务器）…", () => vm.OpenRemoteCopyCommand.Execute(null));
        Add("HTTP站点探测…", () => vm.OpenSiteDiscoveryCommand.Execute(null));
        Add("用户管理…", () => vm.OpenUserManagerCommand.Execute(null));
        Add("Docker 查看/管理…", () => vm.OpenDockerManagerCommand.Execute(null));
        Add("系统防火墙…", () => vm.OpenFirewallManagerCommand.Execute(null));
        menu.Items.Add(new Separator());
        Add("采集快照", () => vm.SnapshotRefreshCommand.Execute(null));
        Add("快照历史", () => vm.OpenSnapshotHistoryCommand.Execute(null));
        menu.Items.Add(new Separator());
        Add("删除", () => vm.DeleteServerCommand.Execute(null));

        menu.IsOpen = true;
    }

    private void OnClearServerSelection(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            vm.SelectedServer = null;
    }

    private void OnNewAiSessionClick(object sender, RoutedEventArgs e) => ViewModel?.NewAiSessionCommand.Execute(null);

    private void OnOpenTerminalClick(object sender, RoutedEventArgs e) => ViewModel?.OpenTerminalCommand.Execute(null);

    private void OnSftpManagerClick(object sender, RoutedEventArgs e) => ViewModel?.OpenSftpManagerCommand.Execute(null);

    private void OnOpenProxiedBrowserClick(object sender, RoutedEventArgs e) => ViewModel?.OpenProxiedBrowserCommand.Execute(null);

    private void OnRemoteCopyClick(object sender, RoutedEventArgs e) => ViewModel?.OpenRemoteCopyCommand.Execute(null);

    private void OnEditClick(object sender, RoutedEventArgs e) => ViewModel?.EditServerCommand.Execute(null);

    private void OnDeleteClick(object sender, RoutedEventArgs e) => ViewModel?.DeleteServerCommand.Execute(null);

    private void OnSnapshotRefreshClick(object sender, RoutedEventArgs e) => ViewModel?.SnapshotRefreshCommand.Execute(null);

    private void OnSnapshotHistoryClick(object sender, RoutedEventArgs e) => ViewModel?.OpenSnapshotHistoryCommand.Execute(null);

    private void OnSiteDiscoveryClick(object sender, RoutedEventArgs e) => ViewModel?.OpenSiteDiscoveryCommand.Execute(null);

    private void OnUserManagerClick(object sender, RoutedEventArgs e) => ViewModel?.OpenUserManagerCommand.Execute(null);

    private void OnMonitorClick(object sender, RoutedEventArgs e) => ViewModel?.OpenMonitorCommand.Execute(null);

    private void OnDockerManagerClick(object sender, RoutedEventArgs e) => ViewModel?.OpenDockerManagerCommand.Execute(null);

    private void OnFirewallManagerClick(object sender, RoutedEventArgs e) => ViewModel?.OpenFirewallManagerCommand.Execute(null);

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView listView)
            return;

        var item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (item == null)
            return;

        listView.SelectedItem = item.DataContext;
        ViewModel?.OpenTerminalCommand.Execute(null);
    }

    private void OnListPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (item != null)
            ServerManageList.SelectedItem = item.DataContext;
    }

    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListView listView)
            return;

        var element = listView.InputHitTest(Mouse.GetPosition(listView)) as DependencyObject;
        if (FindAncestor<ListViewItem>(element) == null)
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
}
