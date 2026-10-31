using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    private void NewAiSessionForServer(SshServerConfig? server)
    {
        if (server is null)
            return;
        StartNewAiSession(
            $"针对以下目标执行运维操作：\n" +
            $"SSH服务器「{server.Name}」(serverId={server.Id}, {server.Host}:{server.Port})\n");
    }

    public void NewAiSessionForDataSource(DataSourceConfig datasource)
    {
        StartNewAiSession(
            $"针对以下目标执行运维操作：\n" +
            $"数据源「{datasource.Name}」(datasourceId={datasource.Id}, {datasource.Type} {datasource.Host}:{datasource.Port})\n");
    }

    private DatasourceManageViewModel CreateDatasourcePage()
    {
        var page = new DatasourceManageViewModel(_configService, services: _services);
        page.NewAiSessionRequested += NewAiSessionForDataSource;
        return page;
    }

    private void StartNewAiSession(string prompt)
    {
        var view = EnsureAgentPage();
        CurrentPage = MainPage.Agent;
        if (IsAgentDetached)
            ActivateAgent();

        var vm = view.ViewModel;
        if (vm.IsBusy)
        {
            _dialogs.Warn("当前 AI 会话正在运行，请先停止或等待完成后再新建会话。", "AI 运维助手");
            return;
        }

        if (!string.IsNullOrWhiteSpace(vm.Input) || vm.HasAttachments)
        {
            if (!_dialogs.Confirm(
                    "AI 运维助手输入框已有内容，是否覆盖并新建会话？",
                    "AI 运维助手", warning: false))
                return;
            vm.ClearAttachments();
            vm.Input = string.Empty;
        }

        _ = StartNewAiSessionAsync(view, prompt);
    }

    private async Task StartNewAiSessionAsync(AgentView view, string prompt)
    {
        try
        {
            await view.ViewModel.NewSessionAsync();
            view.ViewModel.Input = prompt;
            view.FocusComposer();
        }
        catch (Exception ex)
        {
            _dialogs.Error("新建 AI 会话失败：" + ex.Message, "AI 运维助手");
        }
    }
}
