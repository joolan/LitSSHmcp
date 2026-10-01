using System.Windows;

namespace LitSSHmcp.App.Views;

public partial class DatasourceEditWindow : Window
{
    public DatasourceEditWindow()
    {
        InitializeComponent();
    }

    public string GetPassword() => PasswordBox.Password;
}
