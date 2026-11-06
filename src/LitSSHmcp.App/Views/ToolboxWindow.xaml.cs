using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class ToolboxWindow : FluentWindow
{
    public ToolboxWindow(ToolboxViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        WindowLayout.Attach(this, "toolbox.window");
    }
}
