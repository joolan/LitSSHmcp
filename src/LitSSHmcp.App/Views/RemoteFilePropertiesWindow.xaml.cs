using System.Windows;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>远程文件/目录属性对话框。</summary>
public partial class RemoteFilePropertiesWindow : FluentWindow
{
    private readonly RemoteFileItem _item;

    public RemoteFilePropertiesWindow(RemoteFileItem item)
    {
        InitializeComponent();
        _item = item;

        NameText.Text = item.Name;
        PathText.Text = item.FullName;
        TypeText.Text = item.IsDirectory ? "目录" : item.IsSymbolicLink ? "符号链接" : "文件";
        SizeText.Text = item.IsDirectory ? "—" : $"{item.SizeText}（{item.Size} 字节）";
        ModifiedText.Text = item.LastModified == default ? "—" : item.LastModified.ToString("yyyy-MM-dd HH:mm:ss");
        PermissionText.Text = string.IsNullOrEmpty(item.Permissions) ? "—" : item.Permissions;
        OwnerText.Text = string.IsNullOrEmpty(item.Owner) ? "—" : item.Owner;
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_item.FullName); } catch { /* ignore */ }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
