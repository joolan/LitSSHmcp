using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Resource;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class ResourceAnalysisWindow : FluentWindow
{
    private readonly ResourceAnalysisViewModel _viewModel;
    private bool _ready;

    public ResourceAnalysisWindow(ResourceAnalysisViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        WindowLayout.Attach(this, "resource-analysis");
        _ready = true;
    }

    public ResourceAnalysisViewModel ViewModel => _viewModel;

    public void SelectMetric(ResourceMetric metric)
    {
        _viewModel.CurrentMetric = metric;
        Tabs.SelectedIndex = metric switch
        {
            ResourceMetric.Cpu => 1,
            ResourceMetric.Memory => 2,
            ResourceMetric.Disk => 3,
            _ => 0
        };
    }

    public async Task ShowAiPromptAsync(ResourceMetric metric)
    {
        var prompt = await _viewModel.BuildAiPromptAsync(metric);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        var dialog = new ScheduledTaskAiPromptWindow(
            prompt,
            _viewModel.SendPromptToAgentAsync,
            "将把服务器资源和占用分析数据发送到 AI 运维助手。可先修改提示词，再点击“发送”。若当前 AI 会话已有内容，会先确认是否覆盖。")
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _viewModel.CurrentMetric = Tabs.SelectedIndex switch
        {
            1 => ResourceMetric.Cpu,
            2 => ResourceMetric.Memory,
            3 => ResourceMetric.Disk,
            _ => ResourceMetric.Overview
        };
    }

    private void OnAiCurrent(object sender, RoutedEventArgs e)
        => AsyncOperation.Run("生成资源 AI 分析", () => ShowAiPromptAsync(_viewModel.CurrentMetric));

    private void OnAiOverview(object sender, RoutedEventArgs e)
        => AsyncOperation.Run("生成整体资源 AI 分析", () => ShowAiPromptAsync(ResourceMetric.Overview));
}
