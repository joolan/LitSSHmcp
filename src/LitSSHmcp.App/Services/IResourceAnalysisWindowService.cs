using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Views;

namespace LitSSHmcp.App.Services;

public interface IResourceAnalysisWindowService
{
    ResourceAnalysisWindow CreateResourceAnalysisWindow(ResourceAnalysisViewModel viewModel);
}
