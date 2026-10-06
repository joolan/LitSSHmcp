using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.ViewModels;
using Microsoft.Win32;

namespace LitSSHmcp.App.Controls;

/// <summary>远程文件浏览器视图（左侧文件面板）：目录导航、上传/下载（含文件夹）、新建/重命名/删除、编辑回传。</summary>
public partial class RemoteFileBrowserView : UserControl
{
    private bool _loadedOnce;

    public RemoteFileBrowserView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private RemoteFileBrowserViewModel? ViewModel => DataContext as RemoteFileBrowserViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce || ViewModel is null)
            return;
        _loadedOnce = true;
        _ = ViewModel.InitializeAsync();
    }

    private Window? Owner => Window.GetWindow(this);

    private void OnUp(object sender, RoutedEventArgs e) => _ = ViewModel?.GoUpAsync();

    private void OnReload(object sender, RoutedEventArgs e) => _ = ViewModel?.LoadAsync();

    private void OnNewFolder(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null)
            return;
        var name = Views.TextInputDialog.Prompt(Owner, "新建文件夹", "文件夹名称:", "new-folder");
        if (!string.IsNullOrWhiteSpace(name))
            _ = vm.NewFolderAsync(name!);
    }

    private void OnUpload(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null)
            return;
        var dialog = new OpenFileDialog { Title = "上传文件", Multiselect = true, Filter = "所有文件|*.*" };
        if (dialog.ShowDialog(Owner) != true)
            return;
        _ = vm.UploadPathsAsync(dialog.FileNames);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _ = ViewModel?.UploadPathsAsync(paths);
            e.Handled = true;
        }
    }

    private void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel is { } vm)
        {
            _ = vm.LoadAsync(PathBox.Text);
            e.Handled = true;
        }
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView)
            return;
        var item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (item == null)
            return;
        FileList.SelectedItem = item.DataContext;
        OpenSelected();
    }

    private void OnOpen(object sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (ViewModel is not { } vm || vm.SelectedItem is not { } item)
            return;
        if (item.IsDirectory)
        {
            _ = vm.LoadAsync(item.FullName);
            return;
        }
        _ = OpenFileAsync(vm, item);
    }

    private static async Task OpenFileAsync(RemoteFileBrowserViewModel vm, RemoteFileItem item)
    {
        var local = await vm.OpenFileForEditAsync(item);
        if (local is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            vm.StatusMessage = "无法打开文件: " + ex.Message;
        }
    }

    private void OnDownload(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || vm.SelectedItem is not { } item || item.IsDirectory)
            return;
        var dialog = new SaveFileDialog { Title = "下载到", FileName = item.Name, Filter = "所有文件|*.*" };
        if (dialog.ShowDialog(Owner) != true)
            return;
        _ = vm.DownloadAsync(item, dialog.FileName);
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || vm.SelectedItem is not { } item)
            return;
        var name = Views.TextInputDialog.Prompt(Owner, "重命名", "新名称:", item.Name);
        if (!string.IsNullOrWhiteSpace(name) && name != item.Name)
            _ = vm.RenameAsync(item, name!);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || vm.SelectedItem is not { } item)
            return;
        var result = MessageBox.Show(
            $"确定删除远程「{item.Name}」？此操作不可恢复。",
            "删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
            _ = vm.DeleteAsync(new[] { item });
    }

    private void OnListPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (item != null)
            FileList.SelectedItem = item.DataContext;
    }

    private void OnDownloadSelected(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        var items = FileList.SelectedItems.Cast<RemoteFileItem>().ToList();
        if (items.Count == 0)
            return;
        var dialog = new OpenFolderDialog { Title = "下载到文件夹" };
        if (dialog.ShowDialog(Owner) != true)
            return;
        _ = vm.DownloadToDirectoryAsync(items, dialog.FolderName);
    }

    private void OnProperties(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || vm.SelectedItem is not { } item)
            return;
        new Views.RemoteFilePropertiesWindow(vm, item) { Owner = Owner }.ShowDialog();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        var items = SelectedItems();
        if (items.Count > 0)
            vm.CopyItems(items);
    }

    private void OnCut(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        var items = SelectedItems();
        if (items.Count > 0)
            vm.CutItems(items);
    }

    private void OnPaste(object sender, RoutedEventArgs e) => _ = ViewModel?.PasteAsync();

    private void OnCd(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && vm.SelectedItem is { } item)
            vm.RequestCd(item);
    }

    private List<RemoteFileItem> SelectedItems()
        => FileList.SelectedItems.Cast<RemoteFileItem>().ToList();

    // ---- 拖出下载（拖到资源管理器） ----

    private Point _dragStart;
    private bool _maybeDrag;

    private void OnListPreviewLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        var item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        _maybeDrag = e.ClickCount == 1 && item is { IsSelected: true };
    }

    private void OnListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_maybeDrag || e.LeftButton != MouseButtonState.Pressed)
            return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _maybeDrag = false;
        _ = StartDragOutAsync();
    }

    private async Task StartDragOutAsync()
    {
        if (ViewModel is not { } vm)
            return;
        var items = FileList.SelectedItems.Cast<RemoteFileItem>().ToList();
        if (items.Count == 0)
            return;

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "litssh-drag", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var ok = await vm.DownloadToDirectoryAsync(items, dir);
        if (!ok)
            return;

        var paths = System.IO.Directory.GetFileSystemEntries(dir);
        if (paths.Length == 0)
            return;

        var data = new DataObject(DataFormats.FileDrop, paths);
        DragDrop.DoDragDrop(FileList, data, DragDropEffects.Copy);
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
