using System.Windows;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class PortForwardWindow : FluentWindow
{
    private readonly PortForwardViewModel _vm;

    public PortForwardWindow(PortForwardViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        WindowLayout.Attach(this, "port-forward");
        Closed += (_, _) => _ = _vm.SaveAsync();
    }

    private static PortForwardRow? RowOf(object sender)
        => (sender as FrameworkElement)?.DataContext as PortForwardRow;

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            _ = _vm.StartAsync(row);
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            _ = _vm.StopAsync(row);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            _vm.Remove(row);
    }
}
