using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Views;

public partial class SnapshotHistoryWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly SnapshotHistoryViewModel _viewModel;
    private readonly ISnapshotStore _store;
    private readonly IConfigService _configService;
    private readonly string? _initialServerId;
    private readonly long? _initialSnapshotId;
    private bool _suppressServerChanged = true;

    public SnapshotHistoryWindow(ISnapshotStore store, IConfigService configService, string? initialServerId, long? initialSnapshotId = null)
    {
        InitializeComponent();
        _store = store;
        _configService = configService;
        _viewModel = new SnapshotHistoryViewModel(store, configService);
        DataContext = _viewModel;
        _initialServerId = initialServerId;
        _initialSnapshotId = initialSnapshotId;
        Loaded += OnLoaded;
        Closed += (_, _) => Loaded -= OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("加载快照历史", async () =>
        {
            await _viewModel.InitializeAsync(_initialServerId);
            _suppressServerChanged = false;
            await _viewModel.LoadSnapshotsAsync(_initialSnapshotId);
        });

    private void OnServerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressServerChanged)
            return;
        AsyncOperation.Run("加载快照列表", () => _viewModel.LoadSnapshotsAsync());
    }

    private void OnSnapshotChanged(object sender, SelectionChangedEventArgs e) =>
        AsyncOperation.Run("加载快照详情", () => _viewModel.ShowSelectedAsync());

    private void OnRefresh(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("刷新快照列表", () => _viewModel.LoadSnapshotsAsync());

    private void OnTrend(object sender, RoutedEventArgs e) =>
        new SnapshotTrendWindow(_store, _configService, _viewModel.SelectedServer?.Id) { Owner = this }.Show();
}
