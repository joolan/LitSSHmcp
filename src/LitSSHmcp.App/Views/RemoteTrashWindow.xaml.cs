using System.Windows;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using Wpf.Ui.Controls;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;

namespace LitSSHmcp.App.Views;

public partial class RemoteTrashWindow : FluentWindow
{
    private readonly RemoteTrashViewModel _viewModel;
    private readonly IUserDialogService _dialogs = new UserDialogService();

    public RemoteTrashWindow(SshServerConfig server, ISshService ssh)
    {
        InitializeComponent();
        _viewModel = new RemoteTrashViewModel(new RemoteTrashService(server, ssh));
        DataContext = _viewModel;
        Title = "远程回收站 - " + server.Name;
        Loaded += (_, _) => AsyncOperation.Run("读取远程回收站", _viewModel.RefreshAsync);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("读取远程回收站", _viewModel.RefreshAsync);

    private void OnRestore(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("恢复回收站文件", async () =>
        {
            var result = await _viewModel.RestoreAsync();
            if (result.Success)
                _dialogs.Info(result.Message, "恢复完成");
            else
                _dialogs.Error(result.Message, "恢复失败");
        });

    private void OnDeletePermanently(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem is not { } item)
            return;
        if (!_dialogs.Confirm($"彻底删除「{item.Name}」？此操作不可恢复。", "彻底删除",
                warning: true, defaultNo: true))
            return;
        AsyncOperation.Run("彻底删除回收站文件", _viewModel.DeletePermanentlyAsync);
    }

    private void OnEmpty(object sender, RoutedEventArgs e)
    {
        var text = TextInputDialog.Prompt(this, "清空回收站",
            "此操作会彻底删除回收站内全部文件并释放远程空间。请输入“清空”确认：", string.Empty);
        if (!string.Equals(text?.Trim(), "清空", StringComparison.Ordinal))
            return;
        AsyncOperation.Run("清空回收站", _viewModel.EmptyAsync);
    }
}
