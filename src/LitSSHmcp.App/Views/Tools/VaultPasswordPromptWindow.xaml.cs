using System.Windows;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views.Tools;

public partial class VaultPasswordPromptWindow : FluentWindow
{
    public VaultPasswordPromptWindow(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
        Loaded += (_, _) => PasswordBoxInput.Focus();
    }

    public string Password => PasswordBoxInput.Password;

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Password))
        {
            ErrorText.Text = "请输入主密码。";
            return;
        }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
