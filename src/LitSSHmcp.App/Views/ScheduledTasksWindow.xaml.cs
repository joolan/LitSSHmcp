using System.Windows;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class ScheduledTasksWindow : FluentWindow
{
    private readonly ScheduledTasksViewModel _viewModel;

    public ScheduledTasksWindow(ScheduledTasksViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        WindowLayout.Attach(this, "scheduled-tasks");
        Loaded += (_, _) => AsyncOperation.Run("加载计划任务", _viewModel.RefreshAsync);
        _viewModel.ShowResultRequested += OnShowResult;
        Closed += (_, _) => _viewModel.ShowResultRequested -= OnShowResult;
    }

    private void OnShowResult(string title, string headline, string message, bool isError)
        => ResultDialog.Show(this, title, headline, message, isError);

    private void OnAiAnalyzeClick(object sender, RoutedEventArgs e)
        => AsyncOperation.Run("生成 AI 分析请求", async () =>
        {
            var prompt = await _viewModel.BuildAiPromptAsync();
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return;
            }

            var dialog = new ScheduledTaskAiPromptWindow(prompt, _viewModel.SendPromptToAgentAsync)
            {
                Owner = this
            };
            dialog.ShowDialog();
        });

    private void OnEditScriptClick(object sender, RoutedEventArgs e)
        => AsyncOperation.Run("编辑计划任务脚本", async () =>
        {
            var context = await _viewModel.CreateScriptEditContextAsync();
            if (context is null)
            {
                return;
            }

            var editor = new RemoteScriptEditorWindow(context) { Owner = this };
            if (editor.ShowDialog() == true)
            {
                await _viewModel.RefreshAsync();
            }
        });

    private void OnCopyCommandClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_viewModel.SelectedCommand);
        }
        catch
        {
        }
    }
}
