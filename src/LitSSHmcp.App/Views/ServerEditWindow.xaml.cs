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

    public string GetPassword() => (PasswordRow.FieldContent as System.Windows.Controls.PasswordBox)?.Password ?? string.Empty;
    public string GetKeyPassword() => (KeyPasswordRow.FieldContent as System.Windows.Controls.PasswordBox)?.Password ?? string.Empty;
    public string GetSudoPassword() => (SudoPasswordRow.FieldContent as System.Windows.Controls.PasswordBox)?.Password ?? string.Empty;

    private void OnCopyPublicKey(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServerEditViewModel vm && vm.HasPublicKey)
        {
            try { Clipboard.SetText(vm.PublicKeyText); } catch { /* ignore */ }
        }
    }
}