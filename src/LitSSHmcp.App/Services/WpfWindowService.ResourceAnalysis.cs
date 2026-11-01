using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Views;

namespace LitSSHmcp.App.Services;

public sealed partial class WpfWindowService : IResourceAnalysisWindowService
{
    public ResourceAnalysisWindow CreateResourceAnalysisWindow(ResourceAnalysisViewModel viewModel) =>
        new(viewModel) { Topmost = false };
}
