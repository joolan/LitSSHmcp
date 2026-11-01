using LitSSHmcp.App.Services;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Resource;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    private readonly Dictionary<string, ResourceAnalysisWindow> _resourceAnalysisWindows = new();

    private async Task OpenResourceAnalysisAsync(SshServerConfig server, ResourceMetric metric)
    {
        await EnsureResourceAnalysisWindowAsync(server, metric);
    }

    private async Task OpenResourceAiAsync(SshServerConfig server, ResourceMetric metric)
    {
        var window = await EnsureResourceAnalysisWindowAsync(server, metric);
        if (window is null)
        {
            return;
        }

        await window.ShowAiPromptAsync(metric);
    }

    private async Task<ResourceAnalysisWindow?> EnsureResourceAnalysisWindowAsync(
        SshServerConfig server,
        ResourceMetric metric)
    {
        if (_resourceAnalysisWindows.TryGetValue(server.Id, out var existing) && existing.IsLoaded)
        {
            existing.Activate();
            existing.SelectMetric(metric);
            await existing.ViewModel.RefreshAsync();
            return existing;
        }

        if (_windows is not IResourceAnalysisWindowService factory)
        {
            _dialogs.Error("资源分析窗口服务不可用。", "资源分析");
            return null;
        }

        var viewModel = new ResourceAnalysisViewModel(
            server,
            _sshService,
            _dialogs,
            SendPromptToAgentAsync);
        var window = factory.CreateResourceAnalysisWindow(viewModel);
        window.Closed += (_, _) => _resourceAnalysisWindows.Remove(server.Id);
        _resourceAnalysisWindows[server.Id] = window;
        window.Show();
        window.SelectMetric(metric);
        await viewModel.RefreshAsync();
        return window;
    }
}
