using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Storage;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>快照趋势窗口：切换服务器 → 读取最近成功快照 → 画内存/磁盘/Swap/负载趋势。</summary>
public partial class SnapshotTrendWindow : FluentWindow
{
    private readonly SnapshotTrendViewModel _viewModel;
    private readonly string? _initialServerId;
    private bool _suppress;

    public SnapshotTrendWindow(ISnapshotStore store, IConfigService configService, string? initialServerId)
    {
        InitializeComponent();
        _viewModel = new SnapshotTrendViewModel(store, configService);
        DataContext = _viewModel;
        _initialServerId = initialServerId;
        Loaded += OnLoadedAsync;
        Closed += (_, _) => Loaded -= OnLoadedAsync;
    }

    private void OnLoadedAsync(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("加载快照趋势", async () =>
        {
            _suppress = true;
            await _viewModel.InitializeAsync(_initialServerId);
            _suppress = false;
            await ReloadAsync();
        });

    private void OnServerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress)
            return;
        AsyncOperation.Run("切换快照趋势服务器", ReloadAsync);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("刷新快照趋势", ReloadAsync);

    private async Task ReloadAsync()
    {
        await _viewModel.LoadAsync();
        Chart.SetData(_viewModel.Series, _viewModel.Times);
    }
}
