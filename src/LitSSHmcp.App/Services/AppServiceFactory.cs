using System.IO;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Snapshot;
using LitSSHmcp.Core.Services.Snapshot.Collectors;
using LitSSHmcp.Core.Services.RemoteDesktop;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Sync;
using LitSSHmcp.Core.Services.Topology;

namespace LitSSHmcp.App.Services;

/// <summary>WPF 界面按需构造 Core 服务（与 MCP 服务器的 DI 注册等价）。</summary>
public static class AppServiceFactory
{
    public static IConfigService CreateConfigService() => new ConfigService();

    private static PortForwardService? _portForwardService;

    /// <summary>
    /// 端口转发服务（进程级单例）：窗口关闭后隧道仍保活，App 退出时由 DisposePortForwardService 统一停止。
    /// 携带 known-hosts 与主机密钥模式，与其他 SSH 通路一致（TOFU/Strict/Off 按安全设置生效）。
    /// </summary>
    public static PortForwardService PortForwardService
    {
        get
        {
            if (_portForwardService is null)
            {
                var security = new SecurityOptionsProvider();
                Interlocked.CompareExchange(
                    ref _portForwardService,
                    new PortForwardService(new FileSshKnownHostsStore(), () => security.SshHostKey.Mode),
                    null);
            }
            return _portForwardService!;
        }
    }

    /// <summary>App 退出时停止全部端口转发并断开连接（最多等待数秒）。</summary>
    public static void DisposePortForwardService()
        => Interlocked.Exchange(ref _portForwardService, null)?.Dispose();

    private static RemoteDesktopService? _remoteDesktopService;

    /// <summary>
    /// 远程桌面（RDP / VNC）连接服务（进程级单例）：连接时按需复用 <see cref="PortForwardService"/> 建立 SSH 隧道，
    /// 查看器退出后自动拆除隧道。App 退出时由 DisposeRemoteDesktopService 统一断开隧道。
    /// </summary>
    public static RemoteDesktopService RemoteDesktopService
    {
        get
        {
            if (_remoteDesktopService is null)
            {
                Interlocked.CompareExchange(
                    ref _remoteDesktopService,
                    new RemoteDesktopService(PortForwardService, new AuditLogService(new SecurityOptionsProvider())),
                    null);
            }
            return _remoteDesktopService!;
        }
    }

    /// <summary>App 退出时拆除远程桌面的活动隧道。</summary>
    public static void DisposeRemoteDesktopService()
        => Interlocked.Exchange(ref _remoteDesktopService, null)?.Dispose();


    public static ISshService CreateSshService()
    {
        var security = new SecurityOptionsProvider();
        var knownHosts = new FileSshKnownHostsStore();
        var limiter = new TargetLimiter(security, ConfigPaths.TargetStateDir);
        return new SshService(knownHosts, security, limiter);
    }

    /// <summary>快照库（供历史/详情窗口读取）。单飞约束在库内（Running 唯一部分索引）, 跨窗口/进程一致。</summary>
    public static ISnapshotStore CreateSnapshotStore() => new SnapshotStore();

    /// <summary>AI 助手会话/消息库（agent.db）。</summary>
    public static LitSSHmcp.Agent.IContextStore CreateAgentContextStore() => new LitSSHmcp.Agent.ContextStore();

    private static DesktopLoginService? _desktopLogin;

    /// <summary>
    /// 桌面可选登录（评估报告 D1 收尾；进程级单例）：Web 账号库校验 + DPAPI 登录态（跨重启保持），
    /// 仅用于归因（新会话 owner / 审计 UserId），桌面不做权限限制。
    /// </summary>
    public static DesktopLoginService DesktopLogin
    {
        get
        {
            _desktopLogin ??= new DesktopLoginService();
            return _desktopLogin;
        }
    }

    /// <summary>App 内置技能根目录（skills/）：其下每个子目录为一个技能（含 SKILL.md），另含总纲 litssh-mcp-ops-skill。</summary>
    public static string BundledSkillsDir => Path.Combine(AppContext.BaseDirectory, "skills");

    /// <summary>快照服务（App 端手动采集）。注意: 一个实例对应一把内存单飞锁, UI 侧应长期复用同一实例。</summary>
    public static ISnapshotService CreateSnapshotService()
    {
        var security = new SecurityOptionsProvider();
        var knownHosts = new FileSshKnownHostsStore();
        var limiter = new TargetLimiter(security, ConfigPaths.TargetStateDir);
        var ssh = new SshService(knownHosts, security, limiter);
        var store = new SnapshotStore();
        var configService = new ConfigService();
        var mysqlProvider = new MySqlConnectionProvider(configService, knownHosts, security, limiter);
        var collectors = new ISnapshotCollector[]
        {
            new ResourceSnapshotCollector(),
            new PortMapSnapshotCollector(),
            new DockerSnapshotCollector(),
            new NginxTlsSnapshotCollector(),
            new SystemdHealthSnapshotCollector(),
            new SecurityAuditSnapshotCollector()
        };
        return new SnapshotService(store, ssh, new CommandFilterService(security), collectors, configService, mysqlProvider);
    }

    public static IDatasourceDriverRegistry CreateDriverRegistry(IConfigService configService)
    {
        var security = new SecurityOptionsProvider();
        var knownHosts = new FileSshKnownHostsStore();
        var limiter = new TargetLimiter(security, ConfigPaths.TargetStateDir);
        var mysql = new MySqlConnectionProvider(configService, knownHosts, security, limiter);
        var postgres = new PostgresConnectionProvider(configService, knownHosts, security, limiter);
        var redis = new RedisConnectionProvider(configService, knownHosts, security, limiter);
        var drivers = new IDatasourceDriver[] { new MySqlDriver(mysql), new PostgresDriver(postgres), new RedisDriver(redis) };
        return new DatasourceDriverRegistry(drivers);
    }

    /// <summary>
    /// 数据库工作台专用驱动注册表：Redis 走 <see cref="RedisSessionCache"/> 复用连接与 SSH 隧道
    /// （工作台会高频开关连接：分批 SCAN、键元信息、取值各一次调用）。
    /// 测试连接 / 拓扑发现等仍用 <see cref="CreateDriverRegistry"/>（每次新建连接，反映真实连通性）。
    /// </summary>
    public static IDatasourceDriverRegistry CreateWorkbenchDriverRegistry(IConfigService configService)
    {
        var security = new SecurityOptionsProvider();
        var knownHosts = new FileSshKnownHostsStore();
        var limiter = new TargetLimiter(security, ConfigPaths.TargetStateDir);
        var mysql = new MySqlConnectionProvider(configService, knownHosts, security, limiter);
        var postgres = new PostgresConnectionProvider(configService, knownHosts, security, limiter);
        var redis = new RedisSessionCache(new RedisConnectionProvider(configService, knownHosts, security, limiter));
        var drivers = new IDatasourceDriver[] { new MySqlDriver(mysql), new PostgresDriver(postgres), new RedisDriver(redis) };
        return new DatasourceDriverRegistry(drivers);
    }

    public static ITopologyService CreateTopologyService(IConfigService configService)
    {
        var security = new SecurityOptionsProvider();
        var knownHosts = new FileSshKnownHostsStore();
        var limiter = new TargetLimiter(security, ConfigPaths.TargetStateDir);
        var ssh = new SshService(knownHosts, security, limiter);
        var registry = CreateDriverRegistry(configService);
        var store = new TopologyStore();
        return new TopologyService(configService, ssh, registry, store,
            new CommandFilterService(security), new AuditLogService(security), new SnapshotStore());
    }

    /// <summary>资产图谱查询服务（时间线/Diff 读取快照库；与 MCP 端使用同一实现）。</summary>
    public static IAssetGraphQueryService CreateAssetGraphQueryService(IConfigService configService) =>
        new AssetGraphQueryService(CreateTopologyService(configService), CreateSnapshotStore(), configService);

    /// <summary>文件/文件夹同步服务：known-hosts、主机密钥模式（Tofu/Strict/Off）与命令 SSH 通道保持一致。</summary>
    public static SyncService CreateSyncService()
    {
        var security = new SecurityOptionsProvider();
        var knownHosts = new FileSshKnownHostsStore();
        var limiter = new TargetLimiter(security, ConfigPaths.TargetStateDir);
        var ssh = new SshService(knownHosts, security, limiter);
        return new SyncService(knownHosts, security.SshHostKey.Mode, ssh);
    }
}
