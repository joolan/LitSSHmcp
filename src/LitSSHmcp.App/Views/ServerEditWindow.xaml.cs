using System.Windows;

namespace LitSSHmcp.App.Views;

public partial class ServerEditWindow : Window
{
    public ServerEditWindow()
    {
        InitializeComponent();
    }

    public string GetPassword() => PasswordBox.Password;
    public string GetKeyPassword() => KeyPasswordBox.Password;
    public string GetSudoPassword() => SudoPasswordBox.Password;
}