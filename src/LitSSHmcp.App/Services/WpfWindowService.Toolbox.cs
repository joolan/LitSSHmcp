using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Views;

namespace LitSSHmcp.App.Services;

public sealed partial class WpfWindowService
{
    public ToolboxWindow CreateToolboxWindow(ToolboxView view) =>
        new(view) { Topmost = false };

    public ToolboxQuickLaunchWindow CreateToolboxQuickLaunchWindow(ToolboxQuickLaunchViewModel viewModel) =>
        new(viewModel) { Topmost = true };
}
