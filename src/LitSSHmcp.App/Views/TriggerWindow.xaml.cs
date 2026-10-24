using System.ComponentModel;
using System.Windows;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class TriggerWindow : FluentWindow
{
    private readonly TriggerViewModel _vm;

    public TriggerWindow(IAppServices? services = null)
    {
        InitializeComponent();
        _vm = new TriggerViewModel(services: services);
        _vm.ResultRequested += (title, headline, message, isError) =>
            ResultDialog.Show(this, title, headline, message, isError);
        DataContext = _vm;
        WindowLayout.Attach(this, "triggers");
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_vm.ConfirmDiscardOnClose())
            e.Cancel = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnOverview(object sender, RoutedEventArgs e) =>
        new TriggerOverviewWindow { Owner = this }.Show();

    private void OnPickGitDir(object sender, RoutedEventArgs e)
        => PickFolder(sender, item => item.Action.GitDir, (item, path) => item.Action.GitDir = path);

    private void OnPickBackupSource(object sender, RoutedEventArgs e)
        => PickFolder(sender, item => item.Action.SourcePath, (item, path) => item.Action.SourcePath = path);

    private void OnPickBackupDest(object sender, RoutedEventArgs e)
        => PickFolder(sender, item => item.Action.DestDir, (item, path) => item.Action.DestDir = path);

    /// <summary>数据库备份输出目录随执行目标：本机用本地对话框、远程用服务器目录浏览窗口。</summary>
    private void OnPickDbBackupDest(object sender, RoutedEventArgs e)
        => PickFolder(sender, item => item.Action.DestDir, (item, path) => item.Action.DestDir = path);

    /// <summary>按目标选择目录：本机用本地文件夹对话框；远程用服务器目录浏览窗口；结果写回该卡片的动作。</summary>
    private void PickFolder(object sender, Func<TriggerActionItem, string> getCurrent, Action<TriggerActionItem, string> apply)
    {
        if (sender is not FrameworkElement { DataContext: TriggerActionItem item })
            return;

        if (_vm.SelectedTrigger is null) { _vm.StatusMessage = "请先新建一个触发事件。"; return; }

        string? picked;
        if (_vm.SelectedTrigger.TargetType == TriggerTarget.Remote)
        {
            var server = _vm.Servers.FirstOrDefault(s => s.Id == _vm.SelectedTrigger.ServerId);
            if (server is null) { _vm.StatusMessage = "请先在上方选择目标服务器。"; return; }
            picked = RemoteFolderPickerWindow.Show(this, server, getCurrent(item));
        }
        else
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择文件夹" };
            var initial = getCurrent(item);
            if (!string.IsNullOrWhiteSpace(initial) && System.IO.Directory.Exists(initial))
                dialog.InitialDirectory = initial;
            picked = dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        }

        if (!string.IsNullOrWhiteSpace(picked))
            apply(item, picked!);
    }
}
