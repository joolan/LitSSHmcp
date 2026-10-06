using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class ServerMonitorWindow : FluentWindow
{
    private readonly ServerMonitorViewModel _vm;

    public ServerMonitorWindow(ServerMonitorViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        WindowLayout.Attach(this, "server-monitor");
        Loaded += (_, _) => _vm.Start();
        Closed += (_, _) => _vm.Stop();
    }
}
