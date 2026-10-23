using System.Windows;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;
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
        Loaded += (_, _) => AsyncOperation.Run("加载触发器概览", _viewModel.LoadAsync);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("刷新触发器概览", _viewModel.LoadAsync);

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        new TriggerWindow { Owner = this }.ShowDialog();
        AsyncOperation.Run("刷新触发器概览", _viewModel.LoadAsync);
    }
}
