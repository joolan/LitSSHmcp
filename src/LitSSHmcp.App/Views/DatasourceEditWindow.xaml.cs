using System.Windows;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class DatasourceEditWindow : FluentWindow
{
    public DatasourceEditWindow()
    {
        InitializeComponent();
    }

    public string GetPassword() => PasswordBox.Password;
}
