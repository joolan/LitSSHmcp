using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Views;

public partial class SnapshotHistoryWindow : Window
{
    private readonly SnapshotHistoryViewModel _viewModel;
    private readonly string? _initialServerId;
    private readonly long? _initialSnapshotId;
    private bool _suppressServerChanged = true;

    public SnapshotHistoryWindow(ISnapshotStore store, IConfigService configService, string? initialServerId, long? initialSnapshotId = null)
    {
        InitializeComponent();
        _viewModel = new SnapshotHistoryViewModel(store, configService);
        DataContext = _viewModel;
        _initialServerId = initialServerId;
        _initialSnapshotId = initialSnapshotId;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync(_initialServerId);
        _suppressServerChanged = false;
        await _viewModel.LoadSnapshotsAsync(_initialSnapshotId);
    }

    private async void OnServerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressServerChanged)
            return;
        await _viewModel.LoadSnapshotsAsync();
    }

    private async void OnSnapshotChanged(object sender, SelectionChangedEventArgs e) => await _viewModel.ShowSelectedAsync();

    private async void OnRefresh(object sender, RoutedEventArgs e) => await _viewModel.LoadSnapshotsAsync();
}
