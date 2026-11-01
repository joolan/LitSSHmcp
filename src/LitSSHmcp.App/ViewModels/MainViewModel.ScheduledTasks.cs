using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    private readonly Dictionary<string, ScheduledTasksWindow> _scheduledTaskWindows = new();
    private RelayCommand? _openScheduledTasksCommand;

    public ICommand OpenScheduledTasksCommand =>
        _openScheduledTasksCommand ??= new RelayCommand(_ => OpenScheduledTasks(SelectedServer));

    private void OpenScheduledTasks(SshServerConfig? server)
    {
        if (server is null)
        {
            return;
        }

        if (_scheduledTaskWindows.TryGetValue(server.Id, out var existing) && existing.IsLoaded)
        {
            existing.Activate();
            return;
        }

        if (_windows is not IScheduledTasksWindowService factory)
        {
            _dialogs.Error("计划任务窗口服务不可用。", "计划任务");
            return;
        }

        var viewModel = new ScheduledTasksViewModel(
            server,
            _sshService,
            _dialogs,
            _services.CreateAuditLog(),
            SendPromptToAgentAsync);
        var window = factory.CreateScheduledTasksWindow(viewModel);
        window.Closed += (_, _) => _scheduledTaskWindows.Remove(server.Id);
        _scheduledTaskWindows[server.Id] = window;
        window.Show();
    }

    public async Task<bool> SendPromptToAgentAsync(string prompt, bool sendImmediately)
    {
        var view = EnsureAgentPage();
        var viewModel = view.ViewModel;

        try
        {
            CurrentPage = MainPage.Agent;
            if (_agentWindow is { IsLoaded: true })
            {
                ActivateAgent();
            }
            else
            {
                if (_agentDetached)
                {
                    _agentDetached = false;
                    view.SetDetached(false);
                }

                DetachAgent();
            }

            await view.WaitUntilInitializedAsync();

            if (viewModel.IsBusy)
            {
                _dialogs.Warn("当前 AI 会话正在运行，请先停止或等待完成后再新建会话。", "AI 运维助手");
                return false;
            }

            var hasContent = viewModel.Turns.Count > 0 ||
                             !string.IsNullOrWhiteSpace(viewModel.Input) ||
                             viewModel.HasAttachments;
            if (hasContent &&
                !_dialogs.Confirm("AI 运维助手当前会话已有内容，是否新建会话并覆盖？", "AI 运维助手", warning: true))
            {
                return false;
            }

            await viewModel.NewSessionAsync();
            viewModel.Input = prompt;
            _ = view.Dispatcher.BeginInvoke(new Action(view.FocusComposer),
                System.Windows.Threading.DispatcherPriority.Background);
            if (sendImmediately)
            {
                AsyncOperation.Run("发送 AI 提示", viewModel.SendAsync);
            }

            return true;
        }
        catch (Exception ex)
        {
            _dialogs.Error("发送到 AI 运维助手失败：" + ex.Message, "AI 运维助手");
            return false;
        }
    }
}
