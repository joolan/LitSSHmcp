using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;

namespace LitSSHmcp.App.Services;

/// <summary>WPF 界面按需构造 Core 服务（与 MCP 服务器的 DI 注册等价）。</summary>
public static class AppServiceFactory
{
    public static IConfigService CreateConfigService() => new ConfigService();

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
        return new TopologyService(configService, ssh, registry, store);
    }
}
