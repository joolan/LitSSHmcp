using System.Windows;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class SyncWindow : FluentWindow
{
    private readonly SyncViewModel _vm;

    public SyncWindow()
    {
        InitializeComponent();
        _vm = new SyncViewModel();
        _vm.ResultRequested += (title, headline, message, isError) =>
            ResultDialog.Show(this, title, headline, message, isError);
        DataContext = _vm;
        WindowLayout.Attach(this, "sync");
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnPickLocalPath(object sender, RoutedEventArgs e)
    {
        var task = _vm.SelectedTask;
        if (task is null) { _vm.StatusMessage = "请先选择或新建任务。"; return; }

        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择本地文件夹" };
        if (!string.IsNullOrWhiteSpace(task.LocalPath) && System.IO.Directory.Exists(task.LocalPath))
            dialog.InitialDirectory = task.LocalPath;
        if (dialog.ShowDialog(this) == true)
        {
            task.LocalPath = dialog.FolderName;
            _vm.NotifyEdited();
        }
    }

    private void OnPickSourcePath(object sender, RoutedEventArgs e)
    {
        var task = _vm.SelectedTask;
        if (task is null) { _vm.StatusMessage = "请先选择或新建任务。"; return; }
        PickRemote(task.SourceServerId, task.SourceRemotePath, p => task.SourceRemotePath = p);
    }

    private void OnPickTargetPath(object sender, RoutedEventArgs e)
    {
        var task = _vm.SelectedTask;
        if (task is null) { _vm.StatusMessage = "请先选择或新建任务。"; return; }
        PickRemote(task.TargetServerId, task.TargetRemotePath, p => task.TargetRemotePath = p);
    }

    private void PickRemote(string? serverId, string? current, Action<string> apply)
    {
        var server = _vm.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server is null) { _vm.StatusMessage = "请先在上方选择服务器。"; return; }

        var picked = RemoteFolderPickerWindow.Show(this, server, current);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            apply(picked!);
            _vm.NotifyEdited();
        }
    }
}
