using System.Windows;
using LitSSHmcp.App.ViewModels;
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

    private void OnCopyPublicKey(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServerEditViewModel vm && vm.HasPublicKey)
        {
            try { Clipboard.SetText(vm.PublicKeyText); } catch { /* ignore */ }
        }
    }
}