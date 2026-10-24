using System.Windows;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class RemoteDesktopEditWindow : FluentWindow
{
    public RemoteDesktopEditWindow()
    {
        InitializeComponent();
    }

    public string GetPassword() => (PasswordRow.FieldContent as System.Windows.Controls.PasswordBox)?.Password ?? string.Empty;
}
