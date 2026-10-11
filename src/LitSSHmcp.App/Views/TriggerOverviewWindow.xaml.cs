using System.Windows;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Storage;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>触发事件总览 / 编排可视化（只读）。</summary>
public partial class TriggerOverviewWindow : FluentWindow
{
    private readonly TriggerOverviewViewModel _viewModel = new(new ConfigService());

    public TriggerOverviewWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await _viewModel.LoadAsync();

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        new TriggerWindow { Owner = this }.ShowDialog();
        _ = _viewModel.LoadAsync();
    }
}
