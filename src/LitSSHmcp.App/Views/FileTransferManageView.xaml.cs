using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

public partial class FileTransferManageView : UserControl
{
    private readonly IUserDialogService _dialogs = new UserDialogService();

    public FileTransferManageView()
    {
        InitializeComponent();
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnSearchShortcut), true);
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
                Dispatcher.BeginInvoke(new Action(FocusSearch), System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    private FileTransferManageViewModel? ViewModel => DataContext as FileTransferManageViewModel;

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

    private void ClearSearch()
    {
        if (ViewModel is { } vm)
            vm.SearchText = string.Empty;
        FocusSearch();
    }

    private void FocusSearch() => Header.FocusFirstInput();

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        var window = new FtpSiteEditWindow(null) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true)
            _ = vm.AddOrUpdateAsync(window.Site, isNew: true);
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
        => _ = ViewModel?.LoadAsync();

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SiteList.SelectedItem is FileTransferSiteItem item)
            ViewModel?.Open(item);
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
        => Open(ItemOf(sender) ?? ViewModel?.SelectedItem);

    private void OnOpenItemClick(object sender, RoutedEventArgs e)
        => Open(ItemOf(sender));

    private void Open(FileTransferSiteItem? item)
    {
        if (item is not null)
            ViewModel?.Open(item);
    }

    private void OnTestClick(object sender, RoutedEventArgs e)
        => Test(ItemOf(sender) ?? ViewModel?.SelectedItem);

    private void OnTestItemClick(object sender, RoutedEventArgs e)
        => Test(ItemOf(sender));

    private void Test(FileTransferSiteItem? item)
    {
        if (item is null || ViewModel is not { } vm)
            return;
        _ = TestAsync(vm, item);
    }

    private async Task TestAsync(FileTransferManageViewModel vm, FileTransferSiteItem item)
    {
        var result = await vm.TestAsync(item);
        if (result.Success)
            _dialogs.Info(result.Message, "文件传输");
        else
            _dialogs.Warn(result.Message, "文件传输");
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
        => Edit(ItemOf(sender) ?? ViewModel?.SelectedItem);

    private void OnEditItemClick(object sender, RoutedEventArgs e)
        => Edit(ItemOf(sender));

    private void Edit(FileTransferSiteItem? item)
    {
        if (item is null || ViewModel is not { } vm)
            return;
        if (item.Site is not { } site)
        {
            _dialogs.Warn("SFTP 站点来自 SSH 服务器配置，请在「SSH 服务器管理」中编辑。", "文件传输");
            return;
        }

        var window = new FtpSiteEditWindow(site.Clone()) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true)
            _ = vm.AddOrUpdateAsync(window.Site, isNew: false);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
        => Delete(ItemOf(sender) ?? ViewModel?.SelectedItem);

    private void OnDeleteItemClick(object sender, RoutedEventArgs e)
        => Delete(ItemOf(sender));

    private void Delete(FileTransferSiteItem? item)
    {
        if (item is not null && ViewModel is { } vm)
            _ = vm.DeleteAsync(item);
    }

    private static FileTransferSiteItem? ItemOf(object sender)
        => (sender as FrameworkElement)?.DataContext as FileTransferSiteItem;

    private void OnListPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (item is not null)
            SiteList.SelectedItem = item.DataContext;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
