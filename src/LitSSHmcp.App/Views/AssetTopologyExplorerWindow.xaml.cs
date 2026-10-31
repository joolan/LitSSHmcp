using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Topology;

namespace LitSSHmcp.App.Views;

public partial class AssetTopologyExplorerWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly AssetTopologyExplorerViewModel _viewModel;

    public AssetTopologyExplorerWindow(ITopologyService? topology = null)
    {
        InitializeComponent();
        var configService = AppServiceFactory.CreateConfigService();
        var topologyService = topology ?? AppServiceFactory.CreateTopologyService(configService);
        _viewModel = new AssetTopologyExplorerViewModel(
            topologyService,
            new AssetConfigurationSuggestionService(configService, topologyService),
            new AssetConfigurationConsistencyService(configService, topologyService));
        DataContext = _viewModel;
        Loaded += (_, _) => _viewModel.Load();
    }
}
