using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.FileTransfer;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace LitSSHmcp.App.Views;

public partial class FtpManagerWindow : FluentWindow
{
    private const string RemoteEntryFormat = "LitSshFtpEntries";
    private readonly FtpManagerViewModel _vm;
    private readonly IUserDialogService _dialogs = new UserDialogService();
    private Point _localDragStart;
    private Point _remoteDragStart;

    public FtpManagerWindow(FtpManagerViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        WindowLayout.Attach(this, "ftp-manager");
        SelectConflictPolicy(vm.Site.ConflictPolicy);
        Loaded += (_, _) => _ = _vm.InitializeAsync();
        Closed += (_, _) => _ = _vm.DisposeAsync();
    }

    private void OnConnect(object sender, RoutedEventArgs e) => _ = _vm.ConnectAsync();

    private void OnDisconnect(object sender, RoutedEventArgs e) => _ = _vm.DisconnectAsync();

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        _ = _vm.LoadRemoteAsync(_vm.CurrentPath);
        _vm.Local.Load();
    }

    private void OnCancelTransfers(object sender, RoutedEventArgs e) => _vm.Queue.CancelAll();

    private void OnRetryFailed(object sender, RoutedEventArgs e) => _vm.Queue.RetryFailed();

    private void OnLocalUp(object sender, RoutedEventArgs e) => _vm.Local.GoUp();

    private void OnLocalRefresh(object sender, RoutedEventArgs e) => _vm.Local.Load();

    private void OnLocalNewFolder(object sender, RoutedEventArgs e)
    {
        var name = TextInputDialog.Prompt(this, "新建文件夹", "文件夹名", "new-folder");
        if (!string.IsNullOrWhiteSpace(name))
            _vm.Local.NewFolder(name!);
    }

    private void OnLocalDelete(object sender, RoutedEventArgs e)
    {
        if (_vm.Local.SelectedItem is not { } item)
            return;
        if (_dialogs.Confirm($"删除本地「{item.Name}」？", "删除", warning: true))
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

    private void OnRemoteUp(object sender, RoutedEventArgs e) => _ = _vm.GoUpAsync();

    private void OnRemoteRefresh(object sender, RoutedEventArgs e) => _ = _vm.LoadRemoteAsync(_vm.CurrentPath);

    private void OnRemoteNewFolder(object sender, RoutedEventArgs e)
    {
        var name = TextInputDialog.Prompt(this, "新建文件夹", "文件夹名", "new-folder");
        if (!string.IsNullOrWhiteSpace(name))
            _ = _vm.NewFolderAsync(name!);
    }

    private void OnRemoteDelete(object sender, RoutedEventArgs e) => _ = _vm.DeleteSelectedAsync();

    private void OnRemoteRename(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedItem is not { } item)
            return;
        var name = TextInputDialog.Prompt(this, "重命名", "新名称", item.Name);
        if (!string.IsNullOrWhiteSpace(name) &&
            !string.Equals(name, item.Name, StringComparison.Ordinal))
            _ = _vm.RenameSelectedAsync(name!);
    }

    private void OnRemoteDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedItem is not { } item)
            return;
        _ = _vm.OpenAsync(item);
    }

    private void OnRemotePathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            _ = _vm.LoadRemoteAsync(box.Text);
            e.Handled = true;
        }
    }

    private void OnUpload(object sender, RoutedEventArgs e) => _vm.UploadSelected();

    private void OnDownload(object sender, RoutedEventArgs e) => _vm.DownloadSelected();

    private void OnPauseTransfers(object sender, RoutedEventArgs e) => _vm.Queue.Pause();

    private void OnResumeTransfers(object sender, RoutedEventArgs e) => _vm.Queue.Resume();

    private void OnConflictPolicyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConflictPolicyBox.SelectedItem is ComboBoxItem { Tag: string value } &&
            Enum.TryParse<FileTransferConflictPolicy>(value, out var policy))
            _vm.Site.ConflictPolicy = policy;
    }

    private void SelectConflictPolicy(FileTransferConflictPolicy policy)
    {
        foreach (var item in ConflictPolicyBox.Items)
        {
            if (item is ComboBoxItem { Tag: string value } &&
                string.Equals(value, policy.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                ConflictPolicyBox.SelectedItem = item;
                return;
            }
        }
        ConflictPolicyBox.SelectedIndex = 0;
    }

    private void OnLocalSelectionChanged(object sender, SelectionChangedEventArgs e)
        => _vm.Local.SelectedItems = LocalList.SelectedItems.Cast<LocalFsItem>().ToArray();

    private void OnRemoteSelectionChanged(object sender, SelectionChangedEventArgs e)
        => _vm.SelectedItems = RemoteList.SelectedItems.Cast<FtpEntry>().ToArray();

    private void OnLocalPreviewMouseDown(object sender, MouseButtonEventArgs e)
        => _localDragStart = e.GetPosition(LocalList);

    private void OnRemotePreviewMouseDown(object sender, MouseButtonEventArgs e)
        => _remoteDragStart = e.GetPosition(RemoteList);

    private void OnLocalPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;
        var point = e.GetPosition(LocalList);
        if (Math.Abs(point.X - _localDragStart.X) < 6 && Math.Abs(point.Y - _localDragStart.Y) < 6)
            return;
        var items = LocalList.SelectedItems.Cast<LocalFsItem>().ToArray();
        if (items.Length == 0)
            return;
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, items.Select(i => i.FullName).ToArray());
        DragDrop.DoDragDrop(LocalList, data, DragDropEffects.Copy);
    }

    private void OnRemotePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;
        var point = e.GetPosition(RemoteList);
        if (Math.Abs(point.X - _remoteDragStart.X) < 6 && Math.Abs(point.Y - _remoteDragStart.Y) < 6)
            return;
        var items = RemoteList.SelectedItems.Cast<FtpEntry>().ToArray();
        if (items.Length == 0)
            return;
        var data = new DataObject();
        data.SetData(RemoteEntryFormat, items);
        DragDrop.DoDragDrop(RemoteList, data, DragDropEffects.Copy);
    }

    private void OnLocalDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(RemoteEntryFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnLocalDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(RemoteEntryFormat) is not FtpEntry[] entries)
            return;
        _vm.Queue.EnqueueDownload(entries, _vm.Local.CurrentPath);
        e.Handled = true;
    }

    private void OnRemoteDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnRemoteDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return;
        var items = paths
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Select(ToLocalItem)
            .ToArray();
        if (items.Length > 0)
            _vm.Queue.EnqueueUpload(items, _vm.CurrentPath);
        e.Handled = true;
    }

    private static LocalFsItem ToLocalItem(string path)
    {
        var isDirectory = Directory.Exists(path);
        return new LocalFsItem
        {
            Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            FullName = path,
            IsDirectory = isDirectory,
            Size = isDirectory ? 0 : new FileInfo(path).Length,
            LastModified = isDirectory ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path)
        };
    }
}
