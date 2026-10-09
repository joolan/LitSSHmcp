using System.IO;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Snapshot;
using LitSSHmcp.Core.Services.Snapshot.Collectors;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
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


    public static ISshService CreateSshService()
    {
        var security = new SecurityOptionsProvider();
        var knownHosts = new FileSshKnownHostsStore();
        var limiter = new TargetLimiter(security);
        return new SshService(knownHosts, security, limiter);
    }

    /// <summary>快照库（供历史/详情窗口读取）。单飞约束在库内（Running 唯一部分索引）, 跨窗口/进程一致。</summary>
    public static ISnapshotStore CreateSnapshotStore() => new SnapshotStore();

    /// <summary>AI 助手会话/消息库（agent.db）。</summary>
    public static LitSSHmcp.Agent.IContextStore CreateAgentContextStore() => new LitSSHmcp.Agent.ContextStore();

    /// <summary>App 内置技能目录（skills/litssh-mcp-ops-skill）。</summary>
    public static string BundledSkillsDir => Path.Combine(AppContext.BaseDirectory, "skills", "litssh-mcp-ops-skill");

    /// <summary>快照服务（App 端手动采集）。注意: 一个实例对应一把内存单飞锁, UI 侧应长期复用同一实例。</summary>
    public static ISnapshotService CreateSnapshotService()
    {
        var security = new SecurityOptionsProvider();
        var knownHosts = new FileSshKnownHostsStore();
        var limiter = new TargetLimiter(security);
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
        var limiter = new TargetLimiter(security);
        var mysql = new MySqlConnectionProvider(configService, knownHosts, security, limiter);
        var postgres = new PostgresConnectionProvider(configService, knownHosts, security, limiter);
        var redis = new RedisConnectionProvider(configService, knownHosts, security, limiter);
        var drivers = new IDatasourceDriver[] { new MySqlDriver(mysql), new PostgresDriver(postgres), new RedisDriver(redis) };
        return new DatasourceDriverRegistry(drivers);
    }

    public static ITopologyService CreateTopologyService(IConfigService configService)
    {
        var security = new SecurityOptionsProvider();
        var knownHosts = new FileSshKnownHostsStore();
        var limiter = new TargetLimiter(security);
        var ssh = new SshService(knownHosts, security, limiter);
        var registry = CreateDriverRegistry(configService);
        var store = new TopologyStore();
        return new TopologyService(configService, ssh, registry, store,
            new CommandFilterService(security), new AuditLogService(security));
    }
}
