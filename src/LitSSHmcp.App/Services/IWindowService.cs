using System.Windows;
using LitSSHmcp.Agent;
using LitSSHmcp.App.Services.LocalTerminal;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Snapshot;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 窗口创建的唯一出口：ViewModel 通过意图方法获取窗口，不直接引用具体 Window 类型。
/// 只负责构造（Owner/DataContext/Topmost 等默认值），显示/关闭仍由调用方决定。
/// </summary>
public interface IWindowService
{
    SshSessionWindow CreateSessionWindow(object dataContext);
    SshSessionFloatWindow CreateSessionFloat(MainViewModel host, object session);

    TopologyWindow CreateTopologyWindow();
    AuditWindow CreateAuditWindow();
    BatchExecWindow CreateBatchExecWindow(object dataContext);

    ServerMonitorWindow CreateServerMonitorWindow(ServerMonitorViewModel viewModel);
    TerminalLogWindow CreateTerminalLogWindow();
    PortForwardWindow CreatePortForwardWindow(PortForwardViewModel viewModel);
    SyncWindow CreateSyncWindow();
    TriggerWindow CreateTriggerWindow();

    SftpManagerWindow CreateSftpManagerWindow(SftpManagerViewModel viewModel);
    RemoteCopyWindow CreateRemoteCopyWindow(SshServerConfig server);

    AppSettingsWindow CreateAppSettingsWindow(IConfigService configService);
    AgentWindow CreateAgentWindow(
        IConfigService configService,
        IContextStore store,
        string skillsDir,
        Func<string, LocalTerminalSessionViewModel?>? localTerminalResolver = null,
        IAiTerminalBroker? localTerminalBroker = null);

    SnapshotHistoryWindow CreateSnapshotHistoryWindow(
        ISnapshotStore store,
        IConfigService configService,
        string? initialServerId,
        long? initialSnapshotId = null,
        Window? owner = null);

    ServerEditWindow CreateServerEditWindow(Window? owner = null);
    ApplicationEditWindow CreateApplicationEditWindow(Window? owner = null);
    DatasourceEditWindow CreateDatasourceEditWindow(Window? owner = null);
    RemoteDesktopEditWindow CreateRemoteDesktopEditWindow(Window? owner = null);

    DbWorkbenchWindow CreateDbWorkbenchWindow(DataSourceConfig? initial = null, Window? owner = null);
    RemoteDesktopSessionWindow CreateRemoteDesktopSessionWindow(IConfigService configService);
}
