using System.Windows;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class RemoteCopyWindow : FluentWindow
{
    private readonly RemoteCopyViewModel _vm;

    public RemoteCopyWindow(SshServerConfig source, IEnumerable<RemoteFileItem>? items = null)
    {
        InitializeComponent();
        _vm = new RemoteCopyViewModel(source, items) { Owner = this };
        DataContext = _vm;
        Closed += (_, _) => _vm.CancelIfRunning();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
