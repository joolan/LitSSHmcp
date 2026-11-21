using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace LitSSHmcp.App.Views;

/// <summary>SSH 会话管理：多标签窗口（每个服务器一个标签），非模态、不置顶。</summary>
public partial class SshSessionWindow : FluentWindow
{
    private const string TreeWidthKey = "ssh.tree.width";
    private const double DefaultTreeWidth = 210;
    private const double MinTreeWidth = 150;

    public SshSessionWindow()
    {
        InitializeComponent();
        WindowLayout.Attach(this, "ssh-session");
        Loaded += OnLoadedHook;
    }

    private MainViewModel? _vmHooked;

    private void OnLoadedHook(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && !ReferenceEquals(_vmHooked, vm))
        {
            if (_vmHooked is not null)
            {
                _vmHooked.PropertyChanged -= OnVmPropertyChanged;
                _vmHooked.DockedSessions.CollectionChanged -= OnSessionsChanged;
            }
            _vmHooked = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
            vm.DockedSessions.CollectionChanged += OnSessionsChanged;
        }
        TilingScroll.SizeChanged -= OnTilingSizeChanged;
        TilingScroll.SizeChanged += OnTilingSizeChanged;
        UpdateTileHeight();
        UpdateTreeLayout();
        SyncTreeSelection();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SshLayout) or nameof(MainViewModel.SshTileHeight))
            UpdateTileHeight();
        if (e.PropertyName is nameof(MainViewModel.IsSshTreeNavigation))
            UpdateTreeLayout();
        if (e.PropertyName is nameof(MainViewModel.SelectedSession) or nameof(MainViewModel.IsSshTreeNavigation))
            SyncTreeSelection();
    }

    private void OnSessionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateTileHeight();
        Dispatcher.BeginInvoke(new Action(SyncTreeSelection),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void UpdateTreeLayout()
    {
        if (DataContext is not MainViewModel vm)
            return;

        var show = vm.IsSshTreeNavigation;
        SessionTreeSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SessionTreeColumn.Width = show
            ? new GridLength(Math.Max(MinTreeWidth, UiPrefs.GetDouble(TreeWidthKey, DefaultTreeWidth)))
            : new GridLength(0);
    }

    private void OnTreeSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (SessionTreeColumn.ActualWidth >= MinTreeWidth)
            UiPrefs.SetDouble(TreeWidthKey, SessionTreeColumn.ActualWidth);
    }

    private bool _syncingTreeSelection;
    private SessionTreeNode? _selectedTreeNode;
    private SessionTreeNode? _treeContextNode;

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncingTreeSelection || DataContext is not MainViewModel vm)
            return;
        if (e.NewValue is SessionTreeNode { Session: { } session })
        {
            vm.SelectedSession = session;
            Dispatcher.BeginInvoke(new Action(FocusActiveInput),
                System.Windows.Threading.DispatcherPriority.Input);
            return;
        }

        SyncTreeSelection();
    }

    private void SyncTreeSelection()
    {
        if (DataContext is not MainViewModel vm || !vm.IsSshTreeNavigation)
            return;

        var node = FindTreeNode(vm.SessionTree, vm.SelectedSession);
        _syncingTreeSelection = true;
        try
        {
            foreach (var treeNode in FlattenTree(vm.SessionTree))
                treeNode.IsSelected = false;
            _selectedTreeNode = node;
            if (node is not null)
            {
                foreach (var parent in FindTreePath(vm.SessionTree, node))
                    parent.IsExpanded = true;
                node.IsSelected = true;
            }
        }
        finally
        {
            _syncingTreeSelection = false;
        }
    }

    private static IEnumerable<SessionTreeNode> FlattenTree(
        IEnumerable<SessionTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in FlattenTree(node.Children))
                yield return child;
        }
    }

    private void OnTreePreviewLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(
                e.OriginalSource as DependencyObject) is not null)
            return;

        _treeContextNode = null;
        var item = FindAncestor<System.Windows.Controls.TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not SessionTreeNode node)
            return;

        if (node.IsSession && node.Session is not null && DataContext is MainViewModel vm)
        {
            item.IsSelected = true;
            vm.SelectedSession = node.Session;
        }
        else
        {
            node.IsExpanded = !node.IsExpanded;
            item.IsSelected = false;
            e.Handled = true;
        }
    }

    private void OnTreePreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<System.Windows.Controls.TreeViewItem>(e.OriginalSource as DependencyObject);
        _treeContextNode = item?.DataContext as SessionTreeNode;
        if (_treeContextNode is { IsSession: true })
            item!.IsSelected = true;
        else if (item is not null)
            item.IsSelected = false;
    }

    private void OnTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (DataContext is not MainViewModel vm || _treeContextNode is not { } node)
        {
            e.Handled = true;
            return;
        }

        var menu = new ContextMenu();
        if (node.IsSession && node.Session is not null)
        {
            var detach = new System.Windows.Controls.MenuItem { Header = "分离为独立窗口" };
            detach.Click += (_, _) => OnTreeDetach(this, new RoutedEventArgs());
            menu.Items.Add(detach);

            var close = new System.Windows.Controls.MenuItem
            {
                Header = "关闭会话",
                Foreground = FindResource("AppDangerBrush") as System.Windows.Media.Brush
            };
            close.Click += (_, _) => OnTreeClose(this, new RoutedEventArgs());
            menu.Items.Add(close);
        }
        else if (node.IsServer && node.Server is not null)
        {
            var open = new System.Windows.Controls.MenuItem { Header = "打开新会话" };
            open.Click += (_, _) => vm.ConnectTerminal(node.Server);
            menu.Items.Add(open);

            var count = vm.Sessions.OfType<TerminalSessionViewModel>()
                .Count(session => string.Equals(session.Server.Id, node.Server.Id, StringComparison.Ordinal));
            var closeAll = new System.Windows.Controls.MenuItem
            {
                Header = "关闭下属会话",
                IsEnabled = count > 0,
                Foreground = FindResource("AppDangerBrush") as System.Windows.Media.Brush
            };
            closeAll.Click += (_, _) => vm.CloseSessionsForServer(node.Server);
            menu.Items.Add(closeAll);
        }
        else
        {
            e.Handled = true;
            return;
        }

        SessionTree.ContextMenu = menu;
        menu.PlacementTarget = SessionTree;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnTreeDetach(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm &&
            SessionTree.SelectedItem is SessionTreeNode { Session: { } session })
            vm.FloatSession(session);
    }

    private void OnTreeClose(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm &&
            SessionTree.SelectedItem is SessionTreeNode { Session: { } session })
            vm.CloseSessionCommand.Execute(session);
    }

    private static SessionTreeNode? FindTreeNode(
        IEnumerable<SessionTreeNode> nodes, object? session)
    {
        if (session is null)
            return null;
        foreach (var node in nodes)
        {
            if (ReferenceEquals(node.Session, session))
                return node;
            var found = FindTreeNode(node.Children, session);
            if (found is not null)
                return found;
        }
        return null;
    }

    private static IEnumerable<SessionTreeNode> FindTreePath(
        IEnumerable<SessionTreeNode> nodes, SessionTreeNode target)
    {
        foreach (var node in nodes)
        {
            if (ReferenceEquals(node, target))
                yield break;
            var path = FindTreePath(node.Children, target).ToList();
            if (path.Count > 0 || node.Children.Any(child => ReferenceEquals(child, target)))
            {
                yield return node;
                foreach (var parent in path)
                    yield return parent;
                yield break;
            }
        }
    }

    private void OnTilingSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTileHeight();

    /// <summary>平铺卡片高度：内容未溢出时铺满可用高度，溢出时用用户设定行高（出现滚动条）。</summary>
    private void UpdateTileHeight()
    {
        if (DataContext is not MainViewModel vm)
            return;

        var cols = Math.Max(1, vm.SshTileColumns);
        var count = vm.DockedSessions.Count;
        if (count == 0)
        {
            vm.SshTileCardHeight = vm.SshTileHeight;
            return;
        }

        var rows = (int)Math.Ceiling(count / (double)cols);
        var available = TilingScroll.ActualHeight;
        if (available <= 20)
        {
            vm.SshTileCardHeight = vm.SshTileHeight;
            return;
        }

        var per = available / rows - 6;
        vm.SshTileCardHeight = Math.Max(vm.SshTileHeight, per);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 应用级退出（退出程序）时跳过二次确认，保证 Shutdown 不被中断
        if (!AppShutdown.IsShuttingDown && DataContext is MainViewModel { HasSessions: true } vm)
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

    // ---- 布局切换（标签 / 平铺） ----

    private void OnLayoutTabs(object sender, RoutedEventArgs e) => SetLayout(SessionLayout.Tabs);
    private void OnLayoutTile1(object sender, RoutedEventArgs e) => SetLayout(SessionLayout.Tile1);
    private void OnLayoutTile2(object sender, RoutedEventArgs e) => SetLayout(SessionLayout.Tile2);
    private void OnLayoutTile3(object sender, RoutedEventArgs e) => SetLayout(SessionLayout.Tile3);

    private void OnTileHeightDown(object sender, RoutedEventArgs e) => AdjustHeight(-40);
    private void OnTileHeightUp(object sender, RoutedEventArgs e) => AdjustHeight(40);

    private void AdjustHeight(double delta)
    {
        if (DataContext is MainViewModel vm)
            vm.SshTileHeight += delta;
    }

    // ---- 自定义标题栏 ----

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject) is not null)
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

    // ---- 切换标签后自动聚焦会话输入 ----

    private void OnTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.IsSshTabbed)
            return;
        Dispatcher.BeginInvoke(new Action(FocusActiveInput), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void FocusActiveInput()
    {
        // AI 首次打开时会以最小化窗口创建，避免聚焦把窗口重新激活到前台。
        if (WindowState == WindowState.Minimized)
            return;

        var host = Tabs.Template?.FindName("PART_SelectedContentHost", Tabs) as DependencyObject ?? Tabs;
        var terminal = FindDescendant<TerminalTabView>(host);
        if (terminal is not null)
        {
            terminal.FocusInput();
            return;
        }
        var localTerminal = FindDescendant<LocalTerminalTabView>(host);
        if (localTerminal is not null)
        {
            localTerminal.FocusInput();
            return;
        }
        var box = FindDescendant<TextBox>(host, t => !t.IsReadOnly && t.IsVisible);
        box?.Focus();
    }

    private static T? FindDescendant<T>(DependencyObject? root, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        if (root is null)
            return null;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && (predicate is null || predicate(match)))
                return match;
            var found = FindDescendant(child, predicate);
            if (found is not null)
                return found;
        }
        return null;
    }

    private void OnCommandSyncAll(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        var enable = vm.CommandSyncEnabledCount == 0;
        vm.SetCommandSyncForAll(enable);
        vm.StatusMessage = enable
            ? $"已开启全部 SSH 会话命令同步（有效 {vm.CommandSyncEffectiveCount} 个）"
            : "已关闭全部 SSH 会话命令同步";
    }

    private void OnBatchExec(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        var batchVm = new SessionBatchExecViewModel(vm, vm.Servers);
        new SessionBatchExecWindow { DataContext = batchVm, Owner = this }.ShowDialog();
    }

    private void OnLayoutMenu(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        var menu = new System.Windows.Controls.ContextMenu();
        void Add(string header, SessionLayout layout)
        {
            var mi = new System.Windows.Controls.MenuItem { Header = header, IsCheckable = true, IsChecked = vm.SshLayout == layout };
            mi.Click += (_, _) => vm.SshLayout = layout;
            menu.Items.Add(mi);
        }

        Add("标签切换", SessionLayout.Tabs);
        Add("平铺 1 列", SessionLayout.Tile1);
        Add("平铺 2 列", SessionLayout.Tile2);
        Add("平铺 3 列", SessionLayout.Tile3);
        menu.Items.Add(new System.Windows.Controls.Separator());

        var down = new System.Windows.Controls.MenuItem { Header = "行高 -" };
        down.Click += (_, _) => vm.SshTileHeight -= 40;
        menu.Items.Add(down);
        var up = new System.Windows.Controls.MenuItem { Header = "行高 +" };
        up.Click += (_, _) => vm.SshTileHeight += 40;
        menu.Items.Add(up);

        menu.PlacementTarget = sender as UIElement;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnSessionMoreMenu(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        var menu = new ContextMenu();

        var batch = new System.Windows.Controls.MenuItem { Header = "批量执行", IsEnabled = vm.HasSessions };
        batch.Click += (_, _) => OnBatchExec(this, new RoutedEventArgs());
        menu.Items.Add(batch);

        var layout = new System.Windows.Controls.MenuItem { Header = "会话布局" };
        void AddLayout(string header, SessionLayout value)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header, IsCheckable = true, IsChecked = vm.SshLayout == value };
            item.Click += (_, _) => vm.SshLayout = value;
            layout.Items.Add(item);
        }
        AddLayout("标签切换", SessionLayout.Tabs);
        AddLayout("平铺 1 列", SessionLayout.Tile1);
        AddLayout("平铺 2 列", SessionLayout.Tile2);
        AddLayout("平铺 3 列", SessionLayout.Tile3);
        menu.Items.Add(layout);

        var navigation = new System.Windows.Controls.MenuItem { Header = "会话导航" };
        var tabsNavigation = new System.Windows.Controls.MenuItem
        {
            Header = "标签",
            IsCheckable = true,
            IsChecked = !vm.IsSshTreeNavigation
        };
        tabsNavigation.Click += (_, _) => vm.IsSshTreeNavigation = false;
        navigation.Items.Add(tabsNavigation);
        var treeNavigation = new System.Windows.Controls.MenuItem
        {
            Header = "目录树",
            IsCheckable = true,
            IsChecked = vm.IsSshTreeNavigation
        };
        treeNavigation.Click += (_, _) => vm.IsSshTreeNavigation = true;
        navigation.Items.Add(treeNavigation);
        menu.Items.Add(navigation);

        menu.Items.Add(new Separator());
        var sync = new System.Windows.Controls.MenuItem
        {
            Header = vm.CommandSyncEnabledCount == 0 ? "开启全部命令同步" : "关闭全部命令同步",
            IsEnabled = vm.HasCommandSyncSessions,
            ToolTip = vm.CommandSyncToolTip
        };
        sync.Click += (_, _) => OnCommandSyncAll(this, new RoutedEventArgs());
        menu.Items.Add(sync);

        if (vm.IsSshTiled)
        {
            menu.Items.Add(new Separator());
            var down = new System.Windows.Controls.MenuItem { Header = "平铺行高 -" };
            down.Click += (_, _) => vm.SshTileHeight -= 40;
            menu.Items.Add(down);
            var up = new System.Windows.Controls.MenuItem { Header = "平铺行高 +" };
            up.Click += (_, _) => vm.SshTileHeight += 40;
            menu.Items.Add(up);
        }

        menu.PlacementTarget = sender as UIElement;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

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

        var local = new System.Windows.Controls.MenuItem { Header = "本机 PowerShell 终端" };
        local.Click += (_, _) => vm.ConnectLocalTerminal();
        menu.Items.Add(local);
        menu.Items.Add(new System.Windows.Controls.Separator());

        var servers = vm.Servers.Where(s => !s.Disabled).ToList();
        if (servers.Count == 0)
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "（无可用服务器）", IsEnabled = false });
        }
        else
        {
            foreach (var server in servers)
            {
                var terminal = new System.Windows.Controls.MenuItem { Header = $"{server.Name} ({server.Host})" };
                terminal.Click += (_, _) => vm.ConnectTerminal(server);
                menu.Items.Add(terminal);
            }
        }

        menu.Items.Add(new System.Windows.Controls.Separator());
        var temporary = new System.Windows.Controls.MenuItem { Header = "临时 SSH 连接…" };
        temporary.Click += (_, _) => OpenTemporarySsh(vm);
        menu.Items.Add(temporary);

        menu.PlacementTarget = sender as UIElement;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OpenTemporarySsh(MainViewModel vm)
    {
        var dialog = new TemporarySshWindow(vm.Servers, vm.ProbeTemporaryServerAsync)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true && dialog.Result is { } server)
            vm.ConnectTemporaryServer(server);
    }

    // ---- 标签拖拽排序 / 拖出分离 / 双击重命名 ----

    private const string TabDragFormat = "LitSshTab";
    private Point _dragStart;
    private object? _dragItem;
    private bool _dragOutside;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private void OnTabsPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = null;
        var tab = FindAncestor<TabItem>(e.OriginalSource as DependencyObject);
        if (tab is null || FindAncestor<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject) is not null)
            return;
        _dragStart = e.GetPosition(null);
        _dragItem = tab.DataContext;
    }

    private void OnTabsPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed || !HasPassedDragThreshold(e))
            return;
        BeginDrag((DependencyObject)sender);
    }

    // 卡片头拖拽把手（避免与终端内文本选择冲突）
    private void OnCardGripMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = (sender as FrameworkElement)?.DataContext;
        _dragStart = e.GetPosition(null);
    }

    private void OnCardGripMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed || !HasPassedDragThreshold(e))
            return;
        BeginDrag((DependencyObject)sender);
    }

    private bool HasPassedDragThreshold(MouseEventArgs e)
    {
        var pos = e.GetPosition(null);
        return Math.Abs(pos.X - _dragStart.X) >= SystemParameters.MinimumHorizontalDragDistance ||
               Math.Abs(pos.Y - _dragStart.Y) >= SystemParameters.MinimumVerticalDragDistance;
    }

    private void BeginDrag(DependencyObject source)
    {
        var item = _dragItem;
        _dragItem = null;
        if (item is null)
            return;

        _dragOutside = false;
        DragDrop.DoDragDrop(source, new DataObject(TabDragFormat, item), DragDropEffects.Move);

        if (_dragOutside && DataContext is MainViewModel vm)
            vm.FloatSession(item);
        _dragOutside = false;
    }

    /// <summary>拖拽过程中用屏幕坐标判断光标是否离开窗口范围（离开=拖出，松开即分离为独立窗口）。</summary>
    private void OnDragQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed)
        {
            _dragOutside = false;
            return;
        }
        if ((e.KeyStates & DragDropKeyStates.LeftMouseButton) == 0)
            return; // 左键已松开

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

    private void OnDetachItem(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && (sender as FrameworkElement)?.DataContext is { } session)
            vm.FloatSession(session);
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
