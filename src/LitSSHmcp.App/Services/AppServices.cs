using LitSSHmcp.Agent;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.RemoteDesktop;
using LitSSHmcp.Core.Services.Snapshot;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Sync;
using LitSSHmcp.Core.Services.Topology;

namespace LitSSHmcp.App.Services;

/// <summary>
/// App 的最小组合根：集中配置/对话框/窗口/SSH/快照/拓扑等共享服务的创建与生命周期。
/// 仅覆盖顶层宿主需要的服务；其余按需构造的服务仍可由 AppServiceFactory 提供。
/// </summary>
public interface IAppServices
{
    IConfigService Config { get; }
    IUserDialogService Dialogs { get; }
    IWindowService Windows { get; }
    PortForwardService PortForward { get; }

    ISshService CreateSshService();
    IAuditLogService CreateAuditLog();
    ISnapshotService CreateSnapshotService();
    ISnapshotStore CreateSnapshotStore();
    IDatasourceDriverRegistry CreateDriverRegistry();
    IDatasourceDriverRegistry CreateWorkbenchDriverRegistry();
    ITopologyService CreateTopologyService();
    SyncService CreateSyncService();
    RemoteDesktopService RemoteDesktop { get; }
    IContextStore CreateAgentContextStore();
    string BundledSkillsDir { get; }
}

/// <summary>默认实现：底层仍复用 <see cref="AppServiceFactory"/>，但依赖由组合根统一持有。</summary>
public sealed class AppServices : IAppServices
{
    private readonly Lazy<IConfigService> _config = new(AppServiceFactory.CreateConfigService);
    private readonly Lazy<PortForwardService> _portForward = new(() => AppServiceFactory.PortForwardService);

    public AppServices(IUserDialogService? dialogs = null, IWindowService? windows = null)
    {
        Dialogs = dialogs ?? new UserDialogService();
        if (windows is null)
        {
            var wpf = new WpfWindowService { Services = this };
            Windows = wpf;
        }
        else
        {
            Windows = windows;
        }
    }

    public IConfigService Config => _config.Value;
    public IUserDialogService Dialogs { get; }
    public IWindowService Windows { get; }
    public PortForwardService PortForward => _portForward.Value;

    public ISshService CreateSshService() => AppServiceFactory.CreateSshService();
    public IAuditLogService CreateAuditLog() => new AuditLogService();
    public ISnapshotService CreateSnapshotService() => AppServiceFactory.CreateSnapshotService();
    public ISnapshotStore CreateSnapshotStore() => AppServiceFactory.CreateSnapshotStore();
    public IDatasourceDriverRegistry CreateDriverRegistry() => AppServiceFactory.CreateDriverRegistry(Config);
    public IDatasourceDriverRegistry CreateWorkbenchDriverRegistry() => AppServiceFactory.CreateWorkbenchDriverRegistry(Config);
    public ITopologyService CreateTopologyService() => AppServiceFactory.CreateTopologyService(Config);
    public SyncService CreateSyncService() => AppServiceFactory.CreateSyncService();
    public RemoteDesktopService RemoteDesktop => AppServiceFactory.RemoteDesktopService;
    public IContextStore CreateAgentContextStore() => AppServiceFactory.CreateAgentContextStore();
    public string BundledSkillsDir => AppServiceFactory.BundledSkillsDir;
}
