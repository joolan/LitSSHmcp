using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Toolbox;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Views.Tools;

namespace LitSSHmcp.App.Views;

public partial class ToolboxView : UserControl
{
    private const string ToolboxDragFormat = "LitSshToolboxItem";
    private ToolboxViewModel? _viewModel;
    private HostsEditorWindow? _hostsWindow;
    private PasswordGeneratorWindow? _passwordWindow;
    private EncryptedVaultWindow? _vaultWindow;
    private PasswordVaultWindow? _passwordVaultWindow;
    private CertificateToolWindow? _certificateWindow;
    private TextCodecToolWindow? _textCodecWindow;
    private SshKeyToolWindow? _sshKeyWindow;
    private LocalPortToolWindow? _localPortWindow;
    private ToolboxItemViewModel? _dragItem;
    private ToolboxItemViewModel? _dropTarget;
    private Point _dragStart;

    public ToolboxView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += (_, _) => Hook();
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        SearchBox.Clear();
        if (_viewModel is not null)
            _viewModel.SearchText = string.Empty;
        e.Handled = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Hook();
        DetachButton.Visibility = Window.GetWindow(this) is ToolboxWindow
            ? Visibility.Collapsed
            : Visibility.Visible;
        _ = _viewModel?.LoadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.BuiltinRequested -= OnBuiltinRequested;
    }

    private void Hook()
    {
        if (_viewModel is not null)
            _viewModel.BuiltinRequested -= OnBuiltinRequested;

        _viewModel = DataContext as ToolboxViewModel;
        if (_viewModel is not null)
            _viewModel.BuiltinRequested += OnBuiltinRequested;
    }

    private void OnBuiltinRequested(string id)
    {
        if (string.Equals(id, "hosts", StringComparison.OrdinalIgnoreCase))
        {
            if (_hostsWindow is { IsLoaded: true })
            {
                _hostsWindow.Activate();
                return;
            }

            _hostsWindow = new HostsEditorWindow { Owner = Window.GetWindow(this) };
            _hostsWindow.Closed += (_, _) => _hostsWindow = null;
            _hostsWindow.Show();
            return;
        }

        if (string.Equals(id, "password", StringComparison.OrdinalIgnoreCase))
        {
            if (_passwordWindow is { IsLoaded: true })
            {
                _passwordWindow.Activate();
                return;
            }

            _passwordWindow = new PasswordGeneratorWindow { Owner = Window.GetWindow(this) };
            _passwordWindow.Closed += (_, _) => _passwordWindow = null;
            _passwordWindow.Show();
            return;
        }

        if (string.Equals(id, "vault", StringComparison.OrdinalIgnoreCase))
        {
            if (_vaultWindow is { IsLoaded: true })
            {
                _vaultWindow.Activate();
                return;
            }

            try
            {
                _vaultWindow = new EncryptedVaultWindow { Owner = Window.GetWindow(this) };
                _vaultWindow.Closed += (_, _) => _vaultWindow = null;
                _vaultWindow.Show();
                _vaultWindow.Activate();
            }
            catch (Exception ex)
            {
                _vaultWindow = null;
                new UserDialogService().Error("打开加密文件柜失败：" + ex.Message, "工具箱");
            }
            return;
        }

        if (string.Equals(id, "password-vault", StringComparison.OrdinalIgnoreCase))
        {
            if (_passwordVaultWindow is { IsLoaded: true })
            {
                _passwordVaultWindow.Activate();
                return;
            }

            try
            {
                _passwordVaultWindow = new PasswordVaultWindow { Owner = Window.GetWindow(this) };
                _passwordVaultWindow.Closed += (_, _) => _passwordVaultWindow = null;
                _passwordVaultWindow.Show();
                _passwordVaultWindow.Activate();
            }
            catch (Exception ex)
            {
                _passwordVaultWindow = null;
                new UserDialogService().Error("打开密码保管箱失败：" + ex.Message, "工具箱");
            }
        }
        if (string.Equals(id, "certificate", StringComparison.OrdinalIgnoreCase))
        {
            if (_certificateWindow is { IsLoaded: true })
            {
                _certificateWindow.Activate();
                return;
            }

            _certificateWindow = new CertificateToolWindow { Owner = Window.GetWindow(this) };
            _certificateWindow.Closed += (_, _) => _certificateWindow = null;
            _certificateWindow.Show();
        }
        if (string.Equals(id, "text-codec", StringComparison.OrdinalIgnoreCase))
        {
            if (_textCodecWindow is { IsLoaded: true })
            {
                _textCodecWindow.Activate();
                return;
            }

            _textCodecWindow = new TextCodecToolWindow { Owner = Window.GetWindow(this) };
            _textCodecWindow.Closed += (_, _) => _textCodecWindow = null;
            _textCodecWindow.Show();
        }
        if (string.Equals(id, "ssh-key", StringComparison.OrdinalIgnoreCase))
        {
            if (_sshKeyWindow is { IsLoaded: true })
            {
                _sshKeyWindow.Activate();
                return;
            }

            _sshKeyWindow = new SshKeyToolWindow { Owner = Window.GetWindow(this) };
            _sshKeyWindow.Closed += (_, _) => _sshKeyWindow = null;
            _sshKeyWindow.Show();
        }
        if (string.Equals(id, "local-port", StringComparison.OrdinalIgnoreCase))
        {
            if (_localPortWindow is { IsLoaded: true })
            {
                _localPortWindow.Activate();
                return;
            }

            _localPortWindow = new LocalPortToolWindow { Owner = Window.GetWindow(this) };
            _localPortWindow.Closed += (_, _) => _localPortWindow = null;
            _localPortWindow.Show();
        }
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;

        var dialog = new ToolboxItemEditWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.Result is { } item)
            _ = _viewModel.AddItemAsync(item);
    }

    private void OnDetachWindow(object sender, RoutedEventArgs e) => _viewModel?.RequestOpenWindow();

    private void OnCardView(object sender, RoutedEventArgs e) => _viewModel?.SetListView(false);

    private void OnListView(object sender, RoutedEventArgs e) => _viewModel?.SetListView(true);

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = null;
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
            return;
        _dragItem = (sender as FrameworkElement)?.DataContext as ToolboxItemViewModel;
        _dragStart = e.GetPosition(null);
    }

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop((DependencyObject)sender,
            new DataObject(ToolboxDragFormat, item), DragDropEffects.Move);
    }

    private void OnCardActivate(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
            return;
        if ((sender as FrameworkElement)?.DataContext is ToolboxItemViewModel item)
            _ = _viewModel?.LaunchAsync(item);
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var button = sender as FrameworkElement;
        var card = FindAncestor<Border>(button);
        if (button is null || card?.ContextMenu is not { } menu)
            return;

        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnMenuLaunch(object sender, RoutedEventArgs e)
    {
        if (ItemFromMenu(sender) is { } item)
            _ = _viewModel?.LaunchAsync(item);
    }

    private void OnMenuEdit(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || ItemFromMenu(sender) is not { CanEdit: true } item)
            return;

        var dialog = new ToolboxItemEditWindow(item.Model.Clone()) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.Result is { } updated)
            _ = _viewModel.UpdateItemAsync(updated);
    }

    private void OnItemContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;
        var vm = menu.DataContext as ToolboxItemViewModel ??
                 (menu.PlacementTarget as FrameworkElement)?.DataContext as ToolboxItemViewModel;
        var canEdit = vm?.CanEdit == true;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Tag is "edit" or "delete" or "openlocation")
                item.Visibility = canEdit ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnMenuSetGroup(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || ItemFromMenu(sender) is not { } item)
            return;
        var group = TextInputDialog.Prompt(Window.GetWindow(this), "设置分组",
            "分组名称（留空可清除分组）", item.Model.Group);
        if (group is null)
            return;
        _ = _viewModel.SetGroupAsync(item, group);
    }

    private void OnMenuPin(object sender, RoutedEventArgs e)
    {
        if (ItemFromMenu(sender) is { } item)
            _viewModel?.PinCommand.Execute(item);
    }

    private void OnMenuDelete(object sender, RoutedEventArgs e)
    {
        if (ItemFromMenu(sender) is { } item)
            _viewModel?.DeleteCommand.Execute(item);
    }

    private void OnMenuOpenLocation(object sender, RoutedEventArgs e)
    {
        if (ItemFromMenu(sender) is not { } vm)
            return;

        var target = vm.Model.Target;
        try
        {
            if (Directory.Exists(target))
                NativeShell.OpenFolder(target);
            else if (File.Exists(target))
                NativeShell.RevealInExplorer(target);
        }
        catch (Exception ex)
        {
            new UserDialogService().Warn("打开所在位置失败：" + ex.Message, "工具箱");
        }
    }

    private static ToolboxItemViewModel? ItemFromMenu(object sender)
    {
        if (sender is not MenuItem item)
            return null;
        if (item.Parent is ContextMenu menu && menu.PlacementTarget is FrameworkElement target)
            return target.DataContext as ToolboxItemViewModel;
        return item.DataContext as ToolboxItemViewModel;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var internalMove = e.Data.GetDataPresent(ToolboxDragFormat);
        var fileDrop = e.Data.GetDataPresent(DataFormats.FileDrop);
        if (!internalMove && !fileDrop)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = internalMove ? DragDropEffects.Move : DragDropEffects.Copy;
        DropOverlay.Visibility = fileDrop ? Visibility.Visible : Visibility.Collapsed;

        if (internalMove)
            UpdateDropIndicator(e);
        else
            ClearDropIndicator();

        e.Handled = true;
    }

    private void UpdateDropIndicator(DragEventArgs e)
    {
        var card = FindToolboxCard(e.OriginalSource as DependencyObject);
        var target = card?.DataContext as ToolboxItemViewModel;
        var after = card is not null && e.GetPosition(card).X > card.ActualWidth / 2;
        SetDropIndicator(target, after);
    }

    private void SetDropIndicator(ToolboxItemViewModel? target, bool after)
    {
        if (ReferenceEquals(_dropTarget, target))
        {
            if (target is not null)
            {
                target.ShowInsertBefore = !after;
                target.ShowInsertAfter = after;
            }
            return;
        }

        ClearDropIndicator();
        _dropTarget = target;
        if (target is not null)
        {
            target.ShowInsertBefore = !after;
            target.ShowInsertAfter = after;
        }
    }

    private void ClearDropIndicator()
    {
        if (_dropTarget is not null)
        {
            _dropTarget.ShowInsertBefore = false;
            _dropTarget.ShowInsertAfter = false;
            _dropTarget = null;
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        ClearDropIndicator();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (_viewModel is null)
            return;

        if (e.Data.GetDataPresent(ToolboxDragFormat))
        {
            var source = e.Data.GetData(ToolboxDragFormat) as ToolboxItemViewModel;
            var card = FindToolboxCard(e.OriginalSource as DependencyObject);
            var target = card?.DataContext as ToolboxItemViewModel;
            var after = card is not null && e.GetPosition(card).X > card.ActualWidth / 2;
            ClearDropIndicator();
            if (source is not null && target is not null && !ReferenceEquals(source, target))
                AsyncOperation.Run("调整工具顺序", async () => await _viewModel.MoveRelativeAsync(source, target, after));
            e.Handled = true;
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            AsyncOperation.Run("添加工具", async () => await _viewModel.AddFromPathsAsync(paths));
        e.Handled = true;
    }

    private static Border? FindToolboxCard(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is Border { Tag: "ToolboxCard" } card)
                return card;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
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
