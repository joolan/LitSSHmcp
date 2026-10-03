using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;
using Xunit;

namespace LitSSHmcp.Tests;

public class TopologyDiscoveryTests
{
    [Fact]
    public async Task Discovery_creates_pending_nodes_for_unmatched_container_and_db_endpoint()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" } },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" } } }
        };

        var ssh = new FakeSsh
        {
            JavaOutput = string.Empty,
            DockerOutput = "order-svc\tnginx:latest\t0.0.0.0:8080->80/tcp\tUp 2 hours",
            SsOutput = string.Empty,
            ConfigScanOutput = "/opt/order/application.yml:    url: jdbc:mysql://10.9.9.9:3306/orderdb"
        };

        var store = new MemoryTopologyStore();
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new EmptyRegistry(), store,
            new AllowAllCommandFilter(), new NoopAudit());

        var result = await topology.DiscoverAsync(null, null);

        Assert.Contains(result.NewEdges, e => e.From == "app:disc:order-svc" && e.To == "ssh:s1" && e.Type == "runsOn");
        Assert.Contains(result.NewEdges, e => e.From == "ssh:s1" && e.To == "ds:disc:10.9.9.9-3306" && e.Type == "canAccess");
        Assert.Contains(result.UnmatchedEndpoints, d => (d["url"]?.ToString() ?? "").Contains("jdbc:mysql"));
        Assert.True(store.Edges.Count >= 2);
    }

    [Fact]
    public async Task Discovery_skips_disabled_servers()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" },
                new SshServerConfig { Id = "s2", Name = "db1", Host = "10.0.0.2", Disabled = true }
            },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" } } }
        };

        var ssh = new FakeSsh { JavaOutput = string.Empty, DockerOutput = string.Empty, SsOutput = string.Empty };
        var store = new MemoryTopologyStore();
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new EmptyRegistry(), store,
            new AllowAllCommandFilter(), new NoopAudit());

        var result = await topology.DiscoverAsync(null, null);

        Assert.Contains(result.ScannedServers, s => s.StartsWith("web1"));
        Assert.DoesNotContain(result.ScannedServers, s => s.StartsWith("db1"));
        Assert.Contains(result.Notes, n => n.Contains("禁用") && n.Contains("db1"));
    }

    [Fact]
    public async Task Discovery_does_not_duplicate_manual_relation()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" } },
            DataSources = new[] { new DataSourceConfig { Id = "dsX", Name = "orderdb", Type = "mysql", Host = "10.9.9.9", Port = 3306 } },
            Relations = new[] { new RelationConfig { From = "ssh:s1", To = "ds:dsX", Type = "canAccess" } },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" } } }
        };

        var ssh = new FakeSsh
        {
            JavaOutput = string.Empty,
            DockerOutput = string.Empty,
            SsOutput = string.Empty,
            ConfigScanOutput = "/opt/order/application.yml:    url: jdbc:mysql://10.9.9.9:3306/orderdb"
        };

        var store = new MemoryTopologyStore();
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new EmptyRegistry(), store,
            new AllowAllCommandFilter(), new NoopAudit());

        var result = await topology.DiscoverAsync(null, null);

        // 人工已声明 ssh:s1 -> ds:dsX canAccess，发现不应把它计入/写入
        Assert.DoesNotContain(result.NewEdges, e => e.From == "ssh:s1" && e.To == "ds:dsX" && e.Type == "canAccess");
        Assert.DoesNotContain(store.Edges, e => e.From == "ssh:s1" && e.To == "ds:dsX" && e.Type == "canAccess");
        // 但推断出的应用依赖仍应被发现
        Assert.Contains(result.NewEdges, e => e.From == "app:disc:order" && e.To == "ds:dsX" && e.Type == "connectsTo");
    }

    [Fact]
    public async Task Discovery_parses_nginx_proxy_and_mq_endpoints()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" } },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt", "/etc/nginx" } } }
        };

        var ssh = new FakeSsh
        {
            JavaOutput = string.Empty,
            DockerOutput = string.Empty,
            SsOutput = string.Empty,
            ConfigScanOutput =
                "/etc/nginx/conf.d/up.conf:    upstream backend {\n" +
                "/etc/nginx/sites-enabled/foo.conf:    proxy_pass http://backend;\n" +
                "/etc/nginx/sites-enabled/foo.conf:    proxy_pass http://10.0.0.9:8080;\n" +
                "/opt/order/application.yml:    spring.kafka.bootstrap-servers: 10.0.0.7:9092\n" +
                "/opt/order/application.yml:    spring.rabbitmq.addresses: amqp://guest:guest@10.0.0.8:5672/"
        };

        var store = new MemoryTopologyStore();
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new EmptyRegistry(), store,
            new AllowAllCommandFilter(), new NoopAudit());

        var result = await topology.DiscoverAsync(null, null);

        Assert.Contains(result.NewEdges, e => e.From == "app:disc:backend" && e.To == "ssh:s1" && e.Type == "runsOn");
        Assert.Contains(result.NewEdges, e => e.From == "app:disc:foo" && e.To == "app:disc:backend" && e.Type == "connectsTo");
        Assert.Contains(result.NewEdges, e => e.From == "app:disc:foo" && e.To == "app:disc:10.0.0.9-8080" && e.Type == "connectsTo");
        Assert.Contains(result.NewEdges, e => e.From == "app:disc:order" && e.To == "mq:disc:10.0.0.7-9092" && e.Type == "connectsTo");
        Assert.Contains(result.NewEdges, e => e.From == "app:disc:order" && e.To == "mq:disc:10.0.0.8-5672" && e.Type == "connectsTo");
    }

    [Fact]
    public async Task Discovery_detects_service_processes_like_nginx()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" } },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" } } }
        };

        var ssh = new FakeSsh
        {
            ServiceOutput =
                "  1234 root     nginx        nginx: master process /usr/sbin/nginx\n" +
                "  2345 redis    redis-server /usr/bin/redis-server 127.0.0.1:6379\n" +
                "  3456 postgres systemd      /usr/lib/systemd/systemd --user\n", // user=postgres 的桌面进程不应被误报
            ListenOutput =
                "LISTEN 0 511 0.0.0.0:80 0.0.0.0:* users:((\"nginx\",pid=1234,fd=6))\n" +
                "LISTEN 0 128 127.0.0.1:6379 0.0.0.0:* users:((\"redis-server\",pid=2345,fd=6))\n"
        };

        var store = new MemoryTopologyStore();
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new EmptyRegistry(), store,
            new AllowAllCommandFilter(), new NoopAudit());

        var result = await topology.DiscoverAsync(null, null);

        // nginx → 应用节点；redis → 数据源节点；systemd(user=postgres) 不应出现
        Assert.Contains(result.NewEdges, e => e.From == "app:disc:nginx" && e.To == "ssh:s1" && e.Type == "runsOn");
        Assert.Contains(result.NewEdges, e => e.From == "ssh:s1" && e.To == "ds:disc:redis" && e.Type == "canAccess");
        Assert.DoesNotContain(result.NewEdges, e => e.From == "app:disc:systemd" || e.From == "app:disc:python3");
        // 端口/路径信息写入节点信息
        Assert.Contains("80", store.NodeInfos["app:disc:nginx"]);
        Assert.Contains("usr/sbin/nginx", store.NodeInfos["app:disc:nginx"]);
    }

    [Fact]
    public async Task Discovery_attributes_localhost_db_client_to_the_server()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "db1", Host = "10.0.0.1" } },
            DataSources = new[] { new DataSourceConfig { Id = "ds1", Name = "orderdb", Type = "mysql", Host = "10.0.0.1", Port = 3306 } },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" } } }
        };

        var ssh = new FakeSsh();
        var store = new MemoryTopologyStore();
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new FakeRegistry(new FakeMysqlDriver()), store,
            new AllowAllCommandFilter(), new NoopAudit());

        var result = await topology.DiscoverAsync(null, null);

        Assert.Contains(result.NewEdges, e => e.From == "ssh:s1" && e.To == "ds:ds1" && e.Type == "canAccess");
        Assert.DoesNotContain(result.NewEdges, e => e.From.Contains("disc:127") || e.To.Contains("disc:127") || e.From.Contains("disc:localhost"));
    }

    [Fact]
    public async Task Discovery_uses_sudo_when_enabled_and_sudo_configured()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1", SudoType = SudoType.CurrentUser } },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" }, UseSudo = true } }
        };

        var ssh = new FakeSsh { ListenOutput = "LISTEN 0 511 0.0.0.0:80 0.0.0.0:* users:((\"nginx\",pid=1,fd=6))" };
        var topology = new TopologyService(new FakeConfig(config), ssh, new EmptyRegistry(), new MemoryTopologyStore(),
            new AllowAllCommandFilter(), new NoopAudit());

        await topology.DiscoverAsync(null, null);

        Assert.True(ssh.SudoUsed);
    }

    [Fact]
    public async Task Concurrent_discovery_is_rejected()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" } },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" } } }
        };

        var gate = new TaskCompletionSource();
        var ssh = new FakeSsh { Block = gate.Task };
        var topology = new TopologyService(new FakeConfig(config), ssh, new EmptyRegistry(), new MemoryTopologyStore(),
            new AllowAllCommandFilter(), new NoopAudit());

        var first = topology.DiscoverAsync(null, null);
        await ssh.Started.Task; // 第一次发现已开始执行
        await Assert.ThrowsAsync<DiscoveryInProgressException>(() => topology.DiscoverAsync(null, null));

        gate.SetResult();
        Assert.NotNull(await first);
    }

    private sealed class FakeSsh : ISshService
    {
        public string JavaOutput = "";
        public string DockerOutput = "";
        public string ServiceOutput = "";
        public string ListenOutput = "";
        public string SsOutput = "";
        public string ConfigScanOutput = "";

        public bool SudoUsed { get; private set; }
        public TaskCompletionSource Started { get; } = new();
        public Task? Block { get; set; }

        public async Task<CommandResult> ExecuteCommandAsync(SshServerConfig server, string command, CancellationToken ct = default)
        {
            Started.TrySetResult();
            if (Block != null)
                await Block;

            string output =
                command.Contains("ss -Hltnp") ? ListenOutput :
                command.Contains("ps -eo pid,user,comm,args") ? ServiceOutput :
                command.Contains("grep -E") && command.Contains("java") ? JavaOutput :
                command.Contains("docker ps") ? DockerOutput :
                command.Contains("state established") ? SsOutput :
                command.Contains("grep -rE") ? ConfigScanOutput : "";
            return new CommandResult { Success = true, Output = output, ExitCode = 0 };
        }

        public Task<bool> TestConnectionAsync(SshServerConfig server, CancellationToken ct = default) => Task.FromResult(true);
        public Task<ConnectionProbeResult> ProbeConnectionAsync(SshServerConfig server, CancellationToken ct = default) => Task.FromResult(new ConnectionProbeResult { Success = true });
        public Task<CommandResult> ExecuteWithSudoAsync(SshServerConfig server, string command, CancellationToken ct = default)
        {
            SudoUsed = true;
            return ExecuteCommandAsync(server, command, ct);
        }
        public Task<FileTransferResult> UploadFileAsync(SshServerConfig server, string localPath, string remotePath, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FileTransferResult> DownloadFileAsync(SshServerConfig server, string remotePath, string localPath, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RemoteFileListResult> ListRemoteFilesAsync(SshServerConfig server, string remotePath, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeConfig : IConfigService
    {
        private readonly AppConfig _config;
        public FakeConfig(AppConfig config) => _config = config;
        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(_config);
        public Task SaveConfigAsync(AppConfig config) => Task.CompletedTask;
        public string GetConfigPath() => string.Empty;
    }

    private sealed class MemoryTopologyStore : ITopologyStore
    {
        public List<TopologyEdge> Edges { get; } = new();
        public Dictionary<string, string> NodeInfos { get; } = new();
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<TopologyEdge[]> GetEdgesAsync() => Task.FromResult(Edges.ToArray());
        public Task UpsertEdgeAsync(TopologyEdge edge)
        {
            Edges.RemoveAll(e => e.From == edge.From && e.To == edge.To && e.Type == edge.Type);
            Edges.Add(edge);
            return Task.CompletedTask;
        }
        public Task RemoveEdgesByNodeAsync(string nodeId) { Edges.RemoveAll(e => e.From == nodeId || e.To == nodeId); return Task.CompletedTask; }
        public Task RemoveEdgeAsync(string from, string to, string type) { Edges.RemoveAll(e => e.From == from && e.To == to && e.Type == type); return Task.CompletedTask; }
        public Task ReplaceNodeAsync(string oldNodeId, string newNodeId)
        {
            foreach (var e in Edges)
            {
                if (e.From == oldNodeId) e.From = newNodeId;
                if (e.To == oldNodeId) e.To = newNodeId;
            }
            if (NodeInfos.Remove(oldNodeId, out var info)) NodeInfos[newNodeId] = info;
            return Task.CompletedTask;
        }
        public Task ClearPendingAsync()
        {
            Edges.RemoveAll(e => e.From.Contains(":disc:") || e.To.Contains(":disc:"));
            foreach (var key in NodeInfos.Keys.Where(k => k.Contains(":disc:")).ToArray())
                NodeInfos.Remove(key);
            return Task.CompletedTask;
        }
        public Task UpsertNodeInfoAsync(string nodeId, string infoJson) { NodeInfos[nodeId] = infoJson; return Task.CompletedTask; }
        public Task<Dictionary<string, string>> GetNodeInfosAsync() => Task.FromResult(new Dictionary<string, string>(NodeInfos));
    }

    private sealed class AllowAllCommandFilter : ICommandFilterService
    {
        public CommandFilterResult CheckCommand(string command) => CommandFilterResult.Allowed;
    }

    private sealed class NoopAudit : IAuditLogService
    {
        public string? SessionId { get; set; }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task LogCommandAsync(CommandAuditLog log) => Task.CompletedTask;
        public Task<CommandAuditLog[]> GetLogsAsync(string? serverId = null, int limit = 100, string? keyword = null, bool includeHistory = false, int offset = 0, string? sessionId = null, string? tool = null) => Task.FromResult(Array.Empty<CommandAuditLog>());
        public Task LogSqlAsync(SqlAuditLog log) => Task.CompletedTask;
        public Task<SqlAuditLog[]> GetSqlLogsAsync(string? dataSourceId = null, int limit = 100, string? keyword = null, bool includeHistory = false, int offset = 0, string? sessionId = null, string? tool = null) => Task.FromResult(Array.Empty<SqlAuditLog>());
        public Task RecordSessionAsync(AuditSession session) => Task.CompletedTask;
        public Task<AuditSession[]> GetSessionsAsync(int limit = 50) => Task.FromResult(Array.Empty<AuditSession>());
        public Task<AuditVerifyResult> VerifyChainAsync(CancellationToken ct = default) => Task.FromResult(new AuditVerifyResult { Ok = true });
    }

    private sealed class EmptyRegistry : IDatasourceDriverRegistry
    {
        public IDatasourceDriver? Get(string type) => null;
        public IDatasourceDriver GetRequired(string type) => throw new NotSupportedException();
        public IReadOnlyCollection<string> SupportedTypes => Array.Empty<string>();
    }

    private sealed class FakeRegistry : IDatasourceDriverRegistry
    {
        private readonly IDatasourceDriver _driver;
        public FakeRegistry(IDatasourceDriver driver) => _driver = driver;
        public IDatasourceDriver? Get(string type) => _driver;
        public IDatasourceDriver GetRequired(string type) => _driver;
        public IReadOnlyCollection<string> SupportedTypes => new[] { _driver.Type };
    }

    private sealed class FakeMysqlDriver : IDatasourceDriver
    {
        public string Type => "mysql";
        public Task<DatasourceTestResult> TestAsync(DataSourceConfig ds, CancellationToken ct = default) =>
            Task.FromResult(new DatasourceTestResult { Success = true });
        public Task<DatasourceQueryResult> QueryAsync(DataSourceConfig ds, string sql, int maxRows, CancellationToken ct = default) =>
            Task.FromResult(new DatasourceQueryResult
            {
                Success = true,
                Columns = new[] { "Host", "User" },
                Rows = new List<Dictionary<string, object?>>
                {
                    new() { ["Host"] = "127.0.0.1:50000", ["User"] = "app" }
                }
            });
        public Task<DatasourceExecuteResult> ExecuteAsync(DataSourceConfig ds, string sql, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DatasourceQueryResult> ExplainAsync(DataSourceConfig ds, string sql, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DatasourceDiagnosticsResult> DiagnoseAsync(DataSourceConfig ds, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
