using System.Windows;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>远程文件夹选择器：浏览指定服务器的目录，返回当前目录路径。</summary>
public partial class RemoteFolderPickerWindow : FluentWindow
{
    private readonly RemoteFileBrowserViewModel _vm;
    private string? _selected;

    public RemoteFolderPickerWindow(SshServerConfig server, string? initialPath)
    {
        InitializeComponent();
        _vm = new RemoteFileBrowserViewModel(server, new SshService());
        DataContext = _vm;
        Loaded += async (_, _) =>
        {
            await _vm.InitializeAsync();
            if (!string.IsNullOrWhiteSpace(initialPath) && initialPath!.Trim().Length > 0)
                await _vm.LoadAsync(initialPath!.Trim());
        };
        WindowLayout.Attach(this, "remote-folder-picker");
    }

    /// <summary>返回 null=取消；否则为选中的远程目录。</summary>
    public static string? Show(Window? owner, SshServerConfig server, string? initialPath)
    {
        var dlg = new RemoteFolderPickerWindow(server, initialPath) { Owner = owner };
        dlg.ShowDialog();
        return dlg._selected;
    }

    private void OnPick(object sender, RoutedEventArgs e)
    {
        _selected = _vm.CurrentPath;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
