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
    }

    private void OnChooseColumns(object sender, RoutedEventArgs e)
        => LitSSHmcp.App.Controls.ColumnChooser.Show((FrameworkElement)sender, ServerManageList);

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

        Add("打开代理浏览器 (SOCKS)…", () => vm.OpenProxiedBrowserCommand.Execute(null), enabled: !server.Disabled);
        Add("远程互传（服务器↔服务器）…", () => vm.OpenRemoteCopyCommand.Execute(null));
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

    private void OnOpenTerminalClick(object sender, RoutedEventArgs e) => ViewModel?.OpenTerminalCommand.Execute(null);

    private void OnSftpManagerClick(object sender, RoutedEventArgs e) => ViewModel?.OpenSftpManagerCommand.Execute(null);

    private void OnOpenProxiedBrowserClick(object sender, RoutedEventArgs e) => ViewModel?.OpenProxiedBrowserCommand.Execute(null);

    private void OnRemoteCopyClick(object sender, RoutedEventArgs e) => ViewModel?.OpenRemoteCopyCommand.Execute(null);

    private void OnEditClick(object sender, RoutedEventArgs e) => ViewModel?.EditServerCommand.Execute(null);

    private void OnDeleteClick(object sender, RoutedEventArgs e) => ViewModel?.DeleteServerCommand.Execute(null);

    private void OnSnapshotRefreshClick(object sender, RoutedEventArgs e) => ViewModel?.SnapshotRefreshCommand.Execute(null);

    private void OnSnapshotHistoryClick(object sender, RoutedEventArgs e) => ViewModel?.OpenSnapshotHistoryCommand.Execute(null);

    private void OnMonitorClick(object sender, RoutedEventArgs e) => ViewModel?.OpenMonitorCommand.Execute(null);

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
