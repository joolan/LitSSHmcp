using System.Windows;
using LitSSHmcp.App.Services;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class DatabaseCreateUserWindow : FluentWindow
{
    public DatabaseCreateUserWindow()
    {
        InitializeComponent();
        WindowLayout.Attach(this, "db-create-user");
    }

    public string UserName { get; private set; } = string.Empty;
    public string Host { get; private set; } = "%";
    public string Password { get; private set; } = string.Empty;
    public bool CanLogin { get; private set; } = true;

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        var name = UserNameBox.Text.Trim();
        var password = PasswordBox.Password;
        if (name.Length == 0)
        {
            ErrorText.Text = "请输入用户名。";
            return;
        }
        if (password.Length == 0)
        {
            ErrorText.Text = "请输入初始密码。";
            return;
        }

        UserName = name;
        Host = string.IsNullOrWhiteSpace(HostBox.Text) ? "%" : HostBox.Text.Trim();
        Password = password;
        CanLogin = CanLoginBox.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
