using System.Windows;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class JenkinsConnectionWindow : FluentWindow
{
    public JenkinsConnectionWindow()
    {
        InitializeComponent();
    }

    private void OnTokenPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is JenkinsConnectionEditViewModel vm &&
            sender is System.Windows.Controls.PasswordBox passwordBox)
        {
            vm.ApiToken = passwordBox.Password;
        }
    }
}
