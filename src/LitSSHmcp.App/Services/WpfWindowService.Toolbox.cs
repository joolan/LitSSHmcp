using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Views;

namespace LitSSHmcp.App.Services;

public sealed partial class WpfWindowService
{
    public ToolboxWindow CreateToolboxWindow(ToolboxViewModel viewModel) =>
        new(viewModel) { Topmost = false };

    public ToolboxQuickLaunchWindow CreateToolboxQuickLaunchWindow(ToolboxQuickLaunchViewModel viewModel) =>
        new(viewModel) { Topmost = true };
}
