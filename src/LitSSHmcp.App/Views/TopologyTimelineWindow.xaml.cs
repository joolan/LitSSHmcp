using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Views;

public partial class TopologyTimelineWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly string? _initialServerId;
    private readonly IConfigService _configService;
    private readonly TopologyTimelineViewModel _viewModel;
    private bool _ready;

    public TopologyTimelineWindow(string? initialServerId = null)
    {
        InitializeComponent();
        _initialServerId = initialServerId;
        _configService = AppServiceFactory.CreateConfigService();
        _viewModel = new TopologyTimelineViewModel(
            AppServiceFactory.CreateAssetGraphQueryService(_configService), _configService);
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync(_initialServerId);
        _ready = true;
        await _viewModel.LoadAsync();
        ScrollToSelected();
    }

    private async void OnServerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
            return;
        await _viewModel.LoadAsync();
        ScrollToSelected();
    }

    private async void OnLimitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
            return;
        await _viewModel.LoadAsync();
        ScrollToSelected();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadAsync();
        ScrollToSelected();
    }

    private void OnOpenSnapshot(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is null)
            return;
        new SnapshotHistoryWindow(
            AppServiceFactory.CreateSnapshotStore(),
            _configService,
            _viewModel.SelectedServer.Id,
            _viewModel.SelectedPoint?.Point.SnapshotId) { Owner = this }.Show();
    }

    private void ScrollToSelected()
    {
        if (_viewModel.SelectedPoint is null)
            return;
        PointList.SelectedItem = _viewModel.SelectedPoint;
        PointList.ScrollIntoView(_viewModel.SelectedPoint);
    }
}
