using System.Windows;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class ServerEditWindow : FluentWindow
{
    public ServerEditWindow()
    {
        InitializeComponent();
    }

    public string GetPassword() => PasswordBox.Password;
    public string GetKeyPassword() => KeyPasswordBox.Password;
    public string GetSudoPassword() => SudoPasswordBox.Password;
}