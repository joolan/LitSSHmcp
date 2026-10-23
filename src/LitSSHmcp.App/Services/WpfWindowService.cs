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
/// 默认实现：集中构造 WPF 窗口。所有 <c>new *Window()</c> 收敛到此文件，
/// ViewModel 只依赖 <see cref="IWindowService"/>，便于测试替身与后续替换导航方式。
/// </summary>
public sealed class WpfWindowService : IWindowService
{
    public SshSessionWindow CreateSessionWindow(object dataContext) =>
        new() { DataContext = dataContext, Topmost = false };

    public SshSessionFloatWindow CreateSessionFloat(MainViewModel host, object session) =>
        new(host, session) { Topmost = false };

    public TopologyWindow CreateTopologyWindow() => new() { Topmost = false };

    public AuditWindow CreateAuditWindow() => new() { Topmost = false };

    public BatchExecWindow CreateBatchExecWindow(object dataContext) =>
        new() { DataContext = dataContext, Topmost = false };

    public ServerMonitorWindow CreateServerMonitorWindow(ServerMonitorViewModel viewModel) =>
        new(viewModel) { Topmost = false };

    public TerminalLogWindow CreateTerminalLogWindow() => new() { Topmost = false };

    public PortForwardWindow CreatePortForwardWindow(PortForwardViewModel viewModel) =>
        new(viewModel) { Topmost = false };

    public SyncWindow CreateSyncWindow() => new() { Topmost = false };

    public TriggerWindow CreateTriggerWindow() => new() { Topmost = false };

    public SftpManagerWindow CreateSftpManagerWindow(SftpManagerViewModel viewModel) =>
        new(viewModel) { Topmost = false };

    public RemoteCopyWindow CreateRemoteCopyWindow(SshServerConfig server) =>
        new(server) { Topmost = false };

    public AppSettingsWindow CreateAppSettingsWindow(IConfigService configService) =>
        new(configService);

    public AgentWindow CreateAgentWindow(
        IConfigService configService,
        IContextStore store,
        string skillsDir,
        Func<string, LocalTerminalSessionViewModel?>? localTerminalResolver = null,
        IAiTerminalBroker? localTerminalBroker = null) =>
        new(configService, store, skillsDir, localTerminalResolver, localTerminalBroker) { Topmost = false };

    public SnapshotHistoryWindow CreateSnapshotHistoryWindow(
        ISnapshotStore store,
        IConfigService configService,
        string? initialServerId,
        long? initialSnapshotId = null,
        Window? owner = null)
    {
        var window = new SnapshotHistoryWindow(store, configService, initialServerId, initialSnapshotId);
        if (owner is not null)
            window.Owner = owner;
        return window;
    }

    public ServerEditWindow CreateServerEditWindow(Window? owner = null)
    {
        var window = new ServerEditWindow();
        if (owner is not null)
            window.Owner = owner;
        return window;
    }

    public ApplicationEditWindow CreateApplicationEditWindow(Window? owner = null)
    {
        var window = new ApplicationEditWindow();
        if (owner is not null)
            window.Owner = owner;
        return window;
    }

    public DatasourceEditWindow CreateDatasourceEditWindow(Window? owner = null)
    {
        var window = new DatasourceEditWindow();
        if (owner is not null)
            window.Owner = owner;
        return window;
    }

    public RemoteDesktopEditWindow CreateRemoteDesktopEditWindow(Window? owner = null)
    {
        var window = new RemoteDesktopEditWindow();
        if (owner is not null)
            window.Owner = owner;
        return window;
    }

    public DbWorkbenchWindow CreateDbWorkbenchWindow(DataSourceConfig? initial = null, Window? owner = null)
    {
        var window = new DbWorkbenchWindow(initial) { Topmost = false };
        if (owner is not null)
            window.Owner = owner;
        return window;
    }

    public RemoteDesktopSessionWindow CreateRemoteDesktopSessionWindow(IConfigService configService) =>
        new(configService) { Topmost = false };
}
