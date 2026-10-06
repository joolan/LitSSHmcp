using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace LitSSHmcp.App.Views;

public partial class SftpManagerWindow : FluentWindow
{
    private readonly SftpManagerViewModel _vm;

    public SftpManagerWindow(SftpManagerViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        WindowLayout.Attach(this, "sftp-manager");
        Loaded += (_, _) => _vm.Initialize();
    }

    private void OnLocalUp(object sender, RoutedEventArgs e) => _vm.Local.GoUp();

    private void OnLocalRefresh(object sender, RoutedEventArgs e) => _vm.Local.Load();

    private void OnLocalNewFolder(object sender, RoutedEventArgs e)
    {
        var name = Views.TextInputDialog.Prompt(this, "新建文件夹", "文件夹名称:", "new-folder");
        if (!string.IsNullOrWhiteSpace(name))
            _vm.Local.NewFolder(name!);
    }

    private void OnLocalDelete(object sender, RoutedEventArgs e)
    {
        if (_vm.Local.SelectedItem is not { } item)
            return;
        if (System.Windows.MessageBox.Show($"删除本地「{item.Name}」？", "删除", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes)
            _vm.Local.Delete(item);
    }

    private void OnLocalDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.Local.SelectedItem is { IsDirectory: true } item)
            _vm.Local.Load(item.FullName);
    }

    private void OnLocalPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            _vm.Local.Load(box.Text);
            e.Handled = true;
        }
    }

    private void OnRemoteUp(object sender, RoutedEventArgs e) => _ = _vm.Remote.GoUpAsync();

    private void OnRemoteRefresh(object sender, RoutedEventArgs e) => _ = _vm.Remote.LoadAsync();

    private void OnRemoteNewFolder(object sender, RoutedEventArgs e)
    {
        var name = Views.TextInputDialog.Prompt(this, "新建文件夹", "文件夹名称:", "new-folder");
        if (!string.IsNullOrWhiteSpace(name))
            _ = _vm.Remote.NewFolderAsync(name!);
    }

    private void OnRemoteDelete(object sender, RoutedEventArgs e)
    {
        if (_vm.Remote.SelectedItem is not { } item)
            return;
        if (System.Windows.MessageBox.Show($"删除远程「{item.Name}」？此操作不可恢复。", "删除", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes)
            _ = _vm.Remote.DeleteAsync(new[] { item });
    }

    private void OnRemoteDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.Remote.SelectedItem is not { } item)
            return;
        if (item.IsDirectory)
            _ = _vm.Remote.LoadAsync(item.FullName);
        else
            _vm.Queue.EnqueueDownload(new[] { item }, _vm.Local.CurrentPath);
    }

    private void OnRemotePathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            _ = _vm.Remote.LoadAsync(box.Text);
            e.Handled = true;
        }
    }

    private void OnUpload(object sender, RoutedEventArgs e) => _vm.UploadSelected();

    private void OnDownload(object sender, RoutedEventArgs e) => _vm.DownloadSelected();

    private void OnBookmarks(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        if (_vm.Bookmarks.Count == 0)
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "（无书签）", IsEnabled = false });
        }
        else
        {
            foreach (var path in _vm.Bookmarks)
            {
                var item = new System.Windows.Controls.MenuItem { Header = path };
                var target = path;
                item.Click += (_, _) => _ = _vm.Remote.LoadAsync(target);
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new Separator());
        var add = new System.Windows.Controls.MenuItem { Header = "添加当前目录为书签" };
        add.Click += (_, _) => _vm.AddBookmark(_vm.Remote.CurrentPath);
        menu.Items.Add(add);
        var remove = new System.Windows.Controls.MenuItem { Header = "移除当前目录书签" };
        remove.Click += (_, _) => _vm.RemoveBookmark(_vm.Remote.CurrentPath);
        menu.Items.Add(remove);

        menu.PlacementTarget = sender as UIElement;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
