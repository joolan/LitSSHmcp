using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class DockerManagerWindow : FluentWindow
{
    private readonly DockerManagerViewModel _vm;

    public DockerManagerWindow(DockerManagerViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        WindowLayout.Attach(this, "docker-manager");
        Loaded += (_, _) => _ = _vm.RefreshAsync();
    }
}
