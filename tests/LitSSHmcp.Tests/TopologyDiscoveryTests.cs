using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Snapshot;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;
using Xunit;

namespace LitSSHmcp.Tests;

public class TopologyDiscoveryTests
{
    [Fact]
    public async Task Discovery_creates_pending_container_node_but_reports_unmanaged_db_endpoint()
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
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit());

        var result = await topology.DiscoverAsync(null, null);

        Assert.Contains(result.NewEdges, e => e.From == "app:disc:s1:order-svc" && e.To == "ssh:s1" && e.Type == "runsOn");
        // 远程且未匹配到已配置服务器/数据源的 DB 端点: 只登记为未匹配端点, 不建待确认节点
        Assert.DoesNotContain(result.NewEdges, e => e.To.StartsWith("ds:disc:", StringComparison.Ordinal));
        Assert.Contains(result.UnmatchedEndpoints, d => (d["url"]?.ToString() ?? "").Contains("jdbc:mysql"));
        Assert.Contains(result.Notes, n => n.Contains("已跳过"));
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
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new EmptyRegistry(),
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
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit());

        var result = await topology.DiscoverAsync(null, null);

        // 人工已声明 ssh:s1 -> ds:dsX canAccess，发现不应把它计入/写入
        Assert.DoesNotContain(result.NewEdges, e => e.From == "ssh:s1" && e.To == "ds:dsX" && e.Type == "canAccess");
        // 但推断出的应用依赖仍应被发现
        Assert.Contains(result.NewEdges, e => e.From == "app:disc:s1:order" && e.To == "ds:dsX" && e.Type == "connectsTo");
    }

    [Fact]
    public async Task Discovery_parses_nginx_proxy_and_mq_endpoints()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" },
                new SshServerConfig { Id = "s2", Name = "mq1", Host = "10.0.0.7" },
                new SshServerConfig { Id = "s3", Name = "mq2", Host = "10.0.0.8" }
            },
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
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit());

        var result = await topology.DiscoverAsync(null, null);

        Assert.Contains(result.NewEdges, e => e.From == "app:disc:s1:backend" && e.To == "ssh:s1" && e.Type == "runsOn");
        Assert.Contains(result.NewEdges, e => e.From == "app:disc:s1:foo" && e.To == "app:disc:s1:backend" && e.Type == "connectsTo");
        Assert.Contains(result.NewEdges, e => e.From == "app:disc:s1:foo" && e.To == "app:disc:s1:10.0.0.9-8080" && e.Type == "connectsTo");
        Assert.Contains(result.NewEdges, e => e.From == "app:disc:s1:order" && e.To == "mq:disc:10.0.0.7-9092" && e.Type == "connectsTo");
        Assert.Contains(result.NewEdges, e => e.From == "app:disc:s1:order" && e.To == "mq:disc:10.0.0.8-5672" && e.Type == "connectsTo");
    }

    [Fact]
    public async Task Discovery_skips_unmanaged_remote_mq_endpoint()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" } },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" } } }
        };

        var ssh = new FakeSsh
        {
            JavaOutput = string.Empty,
            DockerOutput = string.Empty,
            SsOutput = string.Empty,
            ConfigScanOutput = "/opt/order/application.yml:    spring.rabbitmq.addresses: amqp://guest:guest@rabbit.internal:5672/\n"
        };
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit());

        var result = await topology.DiscoverAsync(null, null);

        // 远程但不对应任何已配置的服务器 → 只登记为未匹配端点, 不建 mq 节点
        Assert.DoesNotContain(result.NewEdges, e => e.To.StartsWith("mq:disc:", StringComparison.Ordinal));
        Assert.Contains(result.UnmatchedEndpoints, d => (d["url"]?.ToString() ?? "").Contains("rabbit.internal"));
        Assert.Contains(result.Notes, n => n.Contains("已跳过"));
    }

    [Fact]
    public async Task Discovery_skips_placeholder_scheme_as_host()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" } },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" } } }
        };

        var ssh = new FakeSsh
        {
            JavaOutput = string.Empty,
            DockerOutput = string.Empty,
            SsOutput = string.Empty,
            ConfigScanOutput =
                "/opt/order/application.yml:    spring.rabbitmq.addresses: amqps://guest:guest@amqps:5672/\n" +
                "/opt/order/application.yml:    spring.rabbitmq.addresses: amqp://guest:guest@amqp:6379/\n"
        };
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit());

        var result = await topology.DiscoverAsync(null, null);

        // 主机名就是协议名 amqp/amqps(占位符) → 不建节点
        Assert.DoesNotContain(result.NewEdges, e => e.To.StartsWith("mq:disc:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Discovery_skips_local_amqp_endpoint_when_port_not_listening()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" } },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" } } }
        };

        var ssh = new FakeSsh
        {
            JavaOutput = string.Empty,
            DockerOutput = string.Empty,
            SsOutput = string.Empty,
            // 只监听 80, 没有 5672/6379
            ListenOutput = "LISTEN 0 511 0.0.0.0:80 0.0.0.0:* users:((\"nginx\",pid=1,fd=6))\n",
            ConfigScanOutput =
                "/opt/order/application.yml:    spring.rabbitmq.addresses: amqp://guest:guest@127.0.0.1:5672/\n" +
                "/opt/order/application.yml:    spring.redis.host: 127.0.0.1\n" +
                "/opt/order/application.yml:    spring.redis.port: 6379\n"
        };
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit());

        var result = await topology.DiscoverAsync(null, null);

        // 本机地址但端口未监听 → 不生成 mq/ds “待确认”节点
        Assert.DoesNotContain(result.NewEdges, e => e.To.StartsWith("mq:disc:", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("已跳过") && n.Contains("5672"));
    }

    [Fact]
    public async Task Discovery_keeps_local_amqp_endpoint_when_port_listening()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" } },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" } } }
        };

        var ssh = new FakeSsh
        {
            JavaOutput = string.Empty,
            DockerOutput = string.Empty,
            SsOutput = string.Empty,
            ListenOutput = "LISTEN 0 128 127.0.0.1:5672 0.0.0.0:* users:((\"beam.smp\",pid=7,fd=6))\n",
            ConfigScanOutput = "/opt/order/application.yml:    spring.rabbitmq.addresses: amqp://guest:guest@127.0.0.1:5672/\n"
        };
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit());

        var result = await topology.DiscoverAsync(null, null);

        Assert.Contains(result.NewEdges, e => e.To == "mq:disc:127.0.0.1-5672" && e.Type == "connectsTo");
    }

    [Fact]
    public async Task Discovery_defaults_amqps_to_5671()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" } },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" } } }
        };

        var ssh = new FakeSsh
        {
            JavaOutput = string.Empty,
            DockerOutput = string.Empty,
            SsOutput = string.Empty,
            ListenOutput = "LISTEN 0 128 127.0.0.1:5671 0.0.0.0:* users:((\"beam.smp\",pid=7,fd=6))\n",
            ConfigScanOutput = "/opt/order/application.yml:    spring.rabbitmq.addresses: amqps://guest:guest@localhost/\n"
        };
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit());

        var result = await topology.DiscoverAsync(null, null);

        // amqps 默认端口是 5671（不是 5672）
        Assert.Contains(result.NewEdges, e => e.To == "mq:disc:localhost-5671" && e.Type == "connectsTo");
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
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit());

        var result = await topology.DiscoverAsync(null, null);

        // nginx → 应用节点；redis → 数据源节点；systemd(user=postgres) 不应出现
        Assert.Contains(result.NewEdges, e => e.From == "app:disc:s1:nginx" && e.To == "ssh:s1" && e.Type == "runsOn");
        Assert.Contains(result.NewEdges, e => e.From == "ssh:s1" && e.To == "ds:disc:local:s1:redis:6379" && e.Type == "canAccess");
        Assert.DoesNotContain(result.NewEdges, e => e.From == "app:disc:s1:systemd" || e.From == "app:disc:s1:python3");
        // 端口/路径信息写入节点信息
    }

    [Fact]
    public async Task Discovery_does_not_cross_attribute_local_tunnel_datasource()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "a", Host = "10.0.0.1" },
                new SshServerConfig { Id = "s2", Name = "b", Host = "10.0.0.2" }
            },
            DataSources = new[]
            {
                new DataSourceConfig { Id = "dsA", Name = "a-mysql", Type = "mysql", Host = "127.0.0.1", Port = 3306, AccessMode = AccessMode.SshTunnel, TunnelServerId = "s1" },
                new DataSourceConfig { Id = "dsB", Name = "b-mysql", Type = "mysql", Host = "127.0.0.1", Port = 3306, AccessMode = AccessMode.SshTunnel, TunnelServerId = "s2" }
            },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" } } }
        };

        var ssh = new FakeSsh
        {
            JavaOutput = string.Empty,
            DockerOutput = string.Empty,
            SsOutput = string.Empty,
            ServiceOutput = "  1234 mysql mysqld /usr/sbin/mysqld\n"
        };
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit());

        var result = await topology.DiscoverAsync(null, null);

        // 两台服务器各自运行本地 mysql，各自的 Host=127.0.0.1 隧道数据源只能归属各自的隧道服务器，不能交叉
        Assert.Contains(result.NewEdges, e => e.From == "ssh:s1" && e.To == "ds:dsA" && e.Type == "canAccess");
        Assert.Contains(result.NewEdges, e => e.From == "ssh:s2" && e.To == "ds:dsB" && e.Type == "canAccess");
        Assert.DoesNotContain(result.NewEdges, e => e.From == "ssh:s1" && e.To == "ds:dsB");
        Assert.DoesNotContain(result.NewEdges, e => e.From == "ssh:s2" && e.To == "ds:dsA");
    }

    [Fact]
    public async Task Discovery_attributes_local_mysql_only_to_its_tunnel_server()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "A", Host = "10.0.0.1" },
                new SshServerConfig { Id = "s2", Name = "B", Host = "10.0.0.2" }
            },
            DataSources = new[]
            {
                // 运行在 B(10.0.0.2) 上, 但跳板选了 A
                new DataSourceConfig { Id = "dsB", Name = "vm-mysql", Type = "mysql", Host = "10.0.0.2", Port = 3306, AccessMode = AccessMode.SshTunnel, TunnelServerId = "s1" },
                // 运行在 A 本机(Host=localhost), 跳板=A
                new DataSourceConfig { Id = "dsA", Name = "a-mysql", Type = "mysql", Host = "127.0.0.1", Port = 3306, AccessMode = AccessMode.SshTunnel, TunnelServerId = "s1" }
            },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" } } }
        };

        var ssh = new FakeSsh
        {
            JavaOutput = string.Empty,
            DockerOutput = string.Empty,
            SsOutput = string.Empty,
            ServiceOutput = "  1 mysql mysqld /usr/sbin/mysqld\n"
        };
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit());

        var result = await topology.DiscoverAsync(null, null);

        // B 跑 mysqld → 命中主机直配的 dsB（即使它的跳板是 A）
        Assert.Contains(result.NewEdges, e => e.From == "ssh:s2" && e.To == "ds:dsB" && e.Type == "canAccess");
        // A 跑 mysqld → 命中 Host=localhost 且跳板=A 的 dsA
        Assert.Contains(result.NewEdges, e => e.From == "ssh:s1" && e.To == "ds:dsA" && e.Type == "canAccess");
        // 不得交叉：B 不命中跳板为 A 的 dsA；A 不命中主机为 B 的 dsB
        Assert.DoesNotContain(result.NewEdges, e => e.From == "ssh:s2" && e.To == "ds:dsA");
        Assert.DoesNotContain(result.NewEdges, e => e.From == "ssh:s1" && e.To == "ds:dsB");
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
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new FakeRegistry(new FakeMysqlDriver()),
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
        var topology = new TopologyService(new FakeConfig(config), ssh, new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit());

        await topology.DiscoverAsync(null, null);

        Assert.True(ssh.SudoUsed);
    }

    [Fact]
    public async Task Graph_uses_curated_relations_and_ignores_discovered_edges()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" },
                new SshServerConfig { Id = "s2", Name = "web2", Host = "10.0.0.2" }
            },
            Relations = new[]
            {
                new RelationConfig { From = "app:a", To = "ssh:s1", Type = "runsOn" }
            }
        };

        var topology = new TopologyService(
            new FakeConfig(config), new FakeSsh(), new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit());

        var graph = await topology.GetGraphAsync();

        Assert.Contains(graph.Edges, e => e.From == "app:a" && e.To == "ssh:s1" && e.Type == "runsOn");
        Assert.DoesNotContain(graph.Edges, e => e.From == "app:a" && e.To == "ssh:s2");
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
        var topology = new TopologyService(new FakeConfig(config), ssh, new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit());

        var first = topology.DiscoverAsync(null, null);
        await ssh.Started.Task; // 第一次发现已开始执行
        await Assert.ThrowsAsync<DiscoveryInProgressException>(() => topology.DiscoverAsync(null, null));

        gate.SetResult();
        Assert.NotNull(await first);
    }

    [Fact]
    public async Task Discovery_ingests_latest_snapshot_into_graph()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" } },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" } } }
        };

        var snapshotStore = new FakeSnapshotStore
        {
            Latest = new ServerSnapshot
            {
                ServerId = "s1",
                ServerName = "web1",
                Status = SnapshotStatus.Succeeded,
                CreatedAt = "2026-01-02T03:04:05Z",
                CompletedAt = "2026-01-02T03:04:05Z",
                DataJson = """
                {
                  "sections": {
                    "portmap": {
                      "status": "ok",
                      "data": {
                        "truncated": false,
                        "listeners": [
                          { "proto": "tcp", "bind": "0.0.0.0", "port": 443, "process": "nginx", "pid": 100, "anyBind": true }
                        ]
                      }
                    },
                    "nginx_tls": {
                      "status": "ok",
                      "data": {
                        "configTruncated": false,
                        "domains": [
                          {
                            "domain": "shop.example.com",
                            "ssl": true,
                            "ports": [443],
                            "certificates": [
                              {
                                "path": "/etc/ssl/shop.pem",
                                "subjectCn": "shop.example.com",
                                "notAfter": "2027-01-01T00:00:00Z",
                                "daysRemaining": 100,
                                "expired": false
                              }
                            ]
                          }
                        ]
                      }
                    }
                  }
                }
                """
            }
        };
        var topology = new TopologyService(
            new FakeConfig(config), new FakeSsh(), new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit(), snapshotStore);

        await topology.DiscoverAsync(null, null);

        var graph = await topology.GetGraphAsync();
        Assert.DoesNotContain(graph.Nodes, n => n.Type == "domain" || n.Type == "port");
        Assert.DoesNotContain(graph.Edges, e => e.Type == "tlsBy");

        var evidence = await topology.GetServerDiscoveryEvidenceAsync("s1");
        Assert.NotNull(evidence);
        Assert.Single(evidence!.Ports);
        Assert.Single(evidence.Domains);
    }

    [Fact]
    public async Task Discovery_same_service_names_on_different_servers_stay_distinct()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" },
                new SshServerConfig { Id = "s2", Name = "web2", Host = "10.0.0.2" }
            },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" } } }
        };

        var ssh = new FakeSsh
        {
            JavaOutput = string.Empty,
            DockerOutput = string.Empty,
            ServiceOutput =
                "  1234 root  nginx  nginx: master process /usr/sbin/nginx\n" +
                "  2345 mysql mysqld /usr/sbin/mysqld\n",
            ListenOutput =
                "LISTEN 0 511 0.0.0.0:80 0.0.0.0:* users:((\"nginx\",pid=1234,fd=6))\n" +
                "LISTEN 0 128 127.0.0.1:3306 0.0.0.0:* users:((\"mysqld\",pid=2345,fd=6))\n"
        };
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit());

        var result = await topology.DiscoverAsync(null, null);

        Assert.Contains(result.NewEdges, e => e.From == "app:disc:s1:nginx" && e.To == "ssh:s1" && e.Type == "runsOn");
        Assert.Contains(result.NewEdges, e => e.From == "app:disc:s2:nginx" && e.To == "ssh:s2" && e.Type == "runsOn");
        Assert.Contains(result.NewEdges, e => e.From == "ssh:s1" && e.To == "ds:disc:local:s1:mysql:3306" && e.Type == "canAccess");
        Assert.Contains(result.NewEdges, e => e.From == "ssh:s2" && e.To == "ds:disc:local:s2:mysql:3306" && e.Type == "canAccess");
        Assert.DoesNotContain(result.NewEdges, e => e.From == "app:disc:nginx");
    }

    [Fact]
    public async Task Discovery_matches_datasource_by_internal_host()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig
                {
                    Id = "s1",
                    Name = "web1",
                    Host = "1.2.3.4",
                    InternalHost = "10.0.0.5"
                }
            },
            DataSources = new[]
            {
                new DataSourceConfig { Id = "ds1", Name = "orderdb", Type = "mysql", Host = "10.0.0.5", Port = 3306 }
            },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" } } }
        };

        var ssh = new FakeSsh
        {
            JavaOutput = string.Empty,
            DockerOutput = string.Empty,
            SsOutput = string.Empty,
            ServiceOutput = "  1 mysql mysqld /usr/sbin/mysqld\n"
        };
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit());

        var result = await topology.DiscoverAsync(null, null);

        Assert.Contains(result.NewEdges, e => e.From == "ssh:s1" && e.To == "ds:ds1" && e.Type == "canAccess");
        Assert.DoesNotContain(result.NewEdges, e => e.To.StartsWith("ds:disc:local:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Discovery_reports_duplicate_asset_candidates()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "web-public", Host = "10.0.0.1", Port = 22 },
                new SshServerConfig { Id = "s2", Name = "web-admin", Host = "10.0.0.1", Port = 22 }
            },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" } } }
        };

        var topology = new TopologyService(
            new FakeConfig(config), new FakeSsh(), new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit());

        var result = await topology.DiscoverAsync(null, null);

        var group = Assert.Single(result.DuplicateCandidates, g => g.AssetType == "ssh");
        Assert.Equal(2, group.Items.Length);
        Assert.Contains(result.Notes, n => n.Contains("疑似重复"));
    }

    [Fact]
    public async Task Discovery_collects_server_identity()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" } },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" } } }
        };

        var ssh = new FakeSsh
        {
            MachineIdOutput = "machine-1\n",
            HostnameOutput = "web1.local\n",
            HostKeyOutput = "256 SHA256:abc123 root@web (ED25519)\n"
        };
        var identityStore = new MemoryIdentityStore();
        var topology = new TopologyService(
            new FakeConfig(config), ssh, new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit(),
            snapshotStore: null, identityStore: identityStore);

        var result = await topology.DiscoverAsync(null, null);

        Assert.Contains(identityStore.Records, r =>
            r.AssetId == "s1" && r.AssetType == "ssh" &&
            r.IdentityKind == AssetIdentityKinds.SshMachineId && r.IdentityValue == "machine-1");
        Assert.Contains(identityStore.Records, r =>
            r.IdentityKind == AssetIdentityKinds.SshHostname && r.IdentityValue == "web1.local");
        Assert.Contains(identityStore.Records, r =>
            r.IdentityKind == AssetIdentityKinds.SshHostKey && r.IdentityValue == "SHA256:abc123");
        Assert.Equal(3, result.IdentityCount);
    }

    [Fact]
    public async Task Discovery_collects_mysql_server_uuid()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" } },
            DataSources = new[]
            {
                new DataSourceConfig { Id = "ds1", Name = "orderdb", Type = "mysql", Host = "10.0.0.1", Port = 3306 }
            },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" } } }
        };

        var identityStore = new MemoryIdentityStore();
        var topology = new TopologyService(
            new FakeConfig(config), new FakeSsh(), new FakeRegistry(new FakeMysqlDriver { ServerUuid = "uuid-1" }), new AllowAllCommandFilter(), new NoopAudit(),
            snapshotStore: null, identityStore: identityStore);

        var result = await topology.DiscoverAsync(null, null);

        Assert.Contains(identityStore.Records, r =>
            r.AssetId == "ds1" && r.AssetType == "datasource" &&
            r.IdentityKind == AssetIdentityKinds.MysqlServerUuid && r.IdentityValue == "uuid-1");
        Assert.Equal(1, result.IdentityCount);
    }

    [Fact]
    public async Task Discovery_reports_identity_conflicts_for_cloned_machine_id()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" },
                new SshServerConfig { Id = "s2", Name = "web2", Host = "10.0.0.2" }
            },
            Security = new SecurityConfig { Discovery = new DiscoveryConfig { AllowedSearchPaths = new[] { "/opt" } } }
        };

        var identityStore = new MemoryIdentityStore();
        var topology = new TopologyService(
            new FakeConfig(config), new FakeSsh { MachineIdOutput = "same-machine\n" },
            new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit(),
            snapshotStore: null, identityStore: identityStore);

        var result = await topology.DiscoverAsync(null, null);

        Assert.Contains(result.IdentityConflicts, c => c.Contains(AssetIdentityKinds.SshMachineId));
        Assert.Contains(result.Notes, n => n.Contains("身份冲突"));
        Assert.Single(identityStore.Records, r => r.IdentityKind == AssetIdentityKinds.SshMachineId);
    }

    [Fact]
    public async Task Graph_merges_profiles_with_same_asset_key_and_resolves_alias()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig
                {
                    Id = "s1", Name = "web-public", Host = "1.2.3.4", Port = 22,
                    AssetKey = "prod-web", CreatedAt = new DateTime(2026, 1, 1)
                },
                new SshServerConfig
                {
                    Id = "s2", Name = "web-internal", Host = "10.0.0.5", Port = 22,
                    AssetKey = "prod-web", CreatedAt = new DateTime(2026, 1, 2)
                }
            },
            Applications = new[]
            {
                new ApplicationConfig
                {
                    Id = "order", Name = "order", AssetKey = "order",
                    RuntimeKind = RuntimeEnvironmentKind.SshServer,
                    RuntimeSshAssetKey = "prod-web"
                }
            }
        };

        var topology = new TopologyService(
            new FakeConfig(config), new FakeSsh(), new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit());

        var graph = await topology.GetGraphAsync();

        var server = Assert.Single(graph.Nodes, n => n.Type == "ssh");
        Assert.Equal("ssh:prod-web", server.Id);
        Assert.Equal(2, ((string[])server.Info["profileIds"]!).Length);
        Assert.Single(graph.Edges, e => e.From == "app:order" && e.Type == "runsOn");

        var dependencies = await topology.GetDependenciesAsync("ssh:s2");
        Assert.Equal("ssh:prod-web", dependencies.Asset?.Id);
    }

    [Fact]
    public async Task Graph_exposes_jump_and_tunnel_access_routes()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig
                {
                    Id = "s1", Name = "target", Host = "10.0.0.1", Port = 22,
                    ProxyJumpServerId = "jump"
                },
                new SshServerConfig { Id = "jump", Name = "jump", Host = "10.0.0.2", Port = 22 }
            },
            DataSources = new[]
            {
                new DataSourceConfig
                {
                    Id = "ds1", Name = "orderdb", Type = "mysql", Host = "10.0.0.3", Port = 3306,
                    AccessMode = AccessMode.SshTunnel, TunnelServerId = "jump"
                }
            }
        };

        var topology = new TopologyService(
            new FakeConfig(config), new FakeSsh(), new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit());

        var graph = await topology.GetGraphAsync();

        Assert.Contains(graph.Edges, e =>
            e.From == "ssh:s1" && e.To == "ssh:jump" && e.Type == "accessedVia");
        Assert.Contains(graph.Edges, e =>
            e.From == "ds:ds1" && e.To == "ssh:jump" && e.Type == "accessedVia");
    }

    [Fact]
    public async Task Curated_graph_uses_runtime_fields_and_ignores_discovered_edges()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1", AssetKey = "web" }
            },
            Applications = new[]
            {
                new ApplicationConfig
                {
                    Id = "docker1", Name = "docker-app", Type = "docker", AssetKey = "docker",
                    RuntimeKind = RuntimeEnvironmentKind.Standalone
                },
                new ApplicationConfig
                {
                    Id = "app1", Name = "order", Type = "java", AssetKey = "order",
                    RuntimeKind = RuntimeEnvironmentKind.SshServer, RuntimeSshAssetKey = "web"
                }
            },
            DataSources = new[]
            {
                new DataSourceConfig
                {
                    Id = "ds1", Name = "orderdb", Type = "mysql", AssetKey = "db",
                    RuntimeKind = RuntimeEnvironmentKind.DockerApplication,
                    RuntimeDockerAssetKey = "docker", RuntimeContainerName = "mysql"
                }
            },
            Relations = new[]
            {
                new RelationConfig { From = "app:order", To = "ds:db", Type = "connectsTo" }
            }
        };

        var topology = new TopologyService(
            new FakeConfig(config), new FakeSsh(), new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit());

        var graph = await topology.GetCuratedGraphAsync();

        Assert.Contains(graph.Edges, e => e.From == "app:order" && e.To == "ssh:web" && e.Type == "runsOn");
        Assert.Contains(graph.Edges, e => e.From == "ds:db" && e.To == "app:docker" && e.Type == "runsIn");
        Assert.Contains(graph.Edges, e => e.From == "app:order" && e.To == "ds:db" && e.Type == "connectsTo");
        Assert.DoesNotContain(graph.Edges, e => e.From == "ds:db" && e.To == "ssh:web" && e.Type == "canAccess");
    }

    [Fact]
    public async Task Server_discovery_evidence_reads_latest_snapshot()
    {
        var snapshotStore = new FakeSnapshotStore
        {
            Latest = new ServerSnapshot
            {
                Id = 7,
                ServerId = "s1",
                ServerName = "web1",
                Status = SnapshotStatus.Succeeded,
                CreatedAt = "2026-01-02T03:04:05Z",
                CompletedAt = "2026-01-02T03:04:05Z",
                DataJson = """
                {
                  "sections": {
                    "portmap": { "status": "ok", "data": { "listeners": [
                      { "proto": "tcp", "bind": "0.0.0.0", "port": 443, "process": "nginx", "pid": 100, "unit": "nginx.service", "exe": "/usr/sbin/nginx" }
                    ] } },
                    "systemd": { "status": "ok", "data": { "units": [
                      { "unit": "nginx.service", "active": "active", "sub": "running", "description": "Nginx" }
                    ] } },
                    "docker": { "status": "ok", "data": { "containers": [
                      { "name": "shop-api", "image": "shop:1.0", "state": "running", "status": "Up", "ports": "8080->80/tcp", "labels": "com.docker.compose.project=shop,com.docker.compose.service=api" }
                    ] } },
                    "nginx_tls": { "status": "ok", "data": { "domains": [
                      { "domain": "shop.example.com", "ssl": true, "ports": [443], "certificates": [
                        { "path": "/etc/ssl/shop.pem", "subjectCn": "shop.example.com", "notAfter": "2027-01-01T00:00:00Z", "fingerprint": "AA:BB:CC" }
                      ] }
                    ] } }
                  }
                }
                """
            }
        };

        var topology = new TopologyService(
            new FakeConfig(new AppConfig
            {
                Servers = new[] { new SshServerConfig { Id = "s1", Name = "web1", Host = "10.0.0.1" } }
            }),
            new FakeSsh(), new EmptyRegistry(),
            new AllowAllCommandFilter(), new NoopAudit(), snapshotStore);

        var evidence = await topology.GetServerDiscoveryEvidenceAsync("s1");

        Assert.NotNull(evidence);
        var port = Assert.Single(evidence!.Ports);
        Assert.Equal(443, port.Port);
        Assert.True(port.SystemdManaged);
        Assert.Single(evidence.Services);
        Assert.Single(evidence.Containers);
        Assert.Equal("shop", evidence.Containers[0].ComposeProject);
        Assert.Equal("api", evidence.Containers[0].ComposeService);
        var domain = Assert.Single(evidence.Domains);
        Assert.Equal("shop.example.com", domain.Domain);
        Assert.Equal("AA:BB:CC", Assert.Single(domain.Certificates).Fingerprint);
    }

    private sealed class MemoryIdentityStore : IAssetIdentityStore
    {
        public List<AssetIdentityRecord> Records { get; } = new();

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<bool> TryUpsertAsync(AssetIdentityRecord record)
        {
            var conflict = AssetIdentityKinds.IsStrong(record.IdentityKind) &&
                           Records.Any(r =>
                               r.AssetType == record.AssetType &&
                               r.IdentityKind == record.IdentityKind &&
                               r.IdentityValue == record.IdentityValue &&
                               r.AssetId != record.AssetId);
            if (conflict)
                return Task.FromResult(false);

            Records.RemoveAll(r =>
                r.AssetId == record.AssetId &&
                r.IdentityKind == record.IdentityKind &&
                r.IdentityValue == record.IdentityValue);
            Records.Add(record);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<AssetIdentityRecord>> GetByAssetAsync(string assetId) =>
            Task.FromResult<IReadOnlyList<AssetIdentityRecord>>(
                Records.Where(r => r.AssetId == assetId).ToArray());

        public Task<IReadOnlyList<AssetIdentityRecord>> GetAllAsync() =>
            Task.FromResult<IReadOnlyList<AssetIdentityRecord>>(Records.ToArray());
    }

    private sealed class FakeSnapshotStore : ISnapshotStore
    {
        public ServerSnapshot? Latest { get; set; }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<long?> BeginAsync(string serverId, string serverName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AppendEventAsync(long snapshotId, string serverId, string kind, string? collector = null, string? message = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CompleteAsync(long snapshotId, string dataJson, double durationMs, string? escalation, int collectorVersion, CancellationToken ct = default) => throw new NotSupportedException();
        public Task FailAsync(long snapshotId, string? error, double durationMs, string? dataJson = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServerSnapshot?> GetLatestAsync(string serverId, CancellationToken ct = default) => Task.FromResult(Latest);
        public Task<ServerSnapshot?> GetByIdAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServerSnapshot?> GetRunningAsync(string serverId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<ServerSnapshot>> GetRecentAsync(string serverId, int limit = 10, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<SnapshotEvent>> GetEventsAsync(long snapshotId, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PruneAsync(string serverId, int keepPerServer, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeSsh : ISshService
    {
        public string JavaOutput = "";
        public string DockerOutput = "";
        public string ServiceOutput = "";
        public string ListenOutput = "";
        public string SsOutput = "";
        public string ConfigScanOutput = "";
        public string HostnameIOutput = "";
        public string MachineIdOutput = "";
        public string HostnameOutput = "";
        public string HostKeyOutput = "";

        public bool SudoUsed { get; private set; }
        public TaskCompletionSource Started { get; } = new();
        public Task? Block { get; set; }

        public async Task<CommandResult> ExecuteCommandAsync(SshServerConfig server, string command, CancellationToken ct = default, int timeoutSeconds = 60)
        {
            Started.TrySetResult();
            if (Block != null)
                await Block;

            string output =
                command.Contains("machine-id") ? MachineIdOutput :
                command.Contains("ssh-keygen -lf") ? HostKeyOutput :
                command.Contains("hostnamectl") ? HostnameOutput :
                command.Contains("ss -Hltnp") ? ListenOutput :
                command.Contains("ps -eo pid,user,comm,args") ? ServiceOutput :
                command.Contains("grep -E") && command.Contains("java") ? JavaOutput :
                command.Contains("docker ps") ? DockerOutput :
                command.Contains("state established") ? SsOutput :
                command.Contains("grep -rE") ? ConfigScanOutput :
                command.Contains("hostname -I") ? HostnameIOutput : "";
            return new CommandResult { Success = true, Output = output, ExitCode = 0 };
        }

        public Task<bool> TestConnectionAsync(SshServerConfig server, CancellationToken ct = default) => Task.FromResult(true);
        public Task<ConnectionProbeResult> ProbeConnectionAsync(SshServerConfig server, CancellationToken ct = default) => Task.FromResult(new ConnectionProbeResult { Success = true });
        public Task<CommandResult> ExecuteWithSudoAsync(SshServerConfig server, string command, CancellationToken ct = default)
        {
            SudoUsed = true;
            return ExecuteCommandAsync(server, command, ct);
        }
        public Task<SshShellSession> OpenShellAsync(SshServerConfig server, string terminalType, uint columns, uint rows, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FileTransferResult> UploadFileAsync(SshServerConfig server, string localPath, string remotePath, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FileTransferResult> DownloadFileAsync(SshServerConfig server, string remotePath, string localPath, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RemoteFileListResult> ListRemoteFilesAsync(SshServerConfig server, string remotePath, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> RemoteFileExistsAsync(SshServerConfig server, string remotePath, CancellationToken ct = default) => Task.FromResult(false);

        public Task<BatchTransferResult> DownloadBatchAsync(SshServerConfig server, IReadOnlyList<string> remotePaths, string localDirectory, bool recursive, int maxFiles, long maxTotalBytes, Func<string, bool>? overwriteResolver = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<BatchTransferResult> UploadBatchAsync(SshServerConfig server, IReadOnlyList<string> localPaths, string remoteDirectory, bool recursive, int maxFiles, long maxFileBytes, long maxTotalBytes, Func<string, bool>? overwriteResolver = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<CommandResult> ExecuteStreamingAsync(SshServerConfig server, string command, Action<string>? onStdout = null, Action<string>? onStderr = null, string? stdinText = null, CancellationToken ct = default, int timeoutSeconds = 120) => throw new NotSupportedException();

        public Task<RemoteCopyResult> CopyRemoteToRemoteAsync(RemoteCopyRequest request, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeConfig : IConfigService
    {
        private readonly AppConfig _config;
        public FakeConfig(AppConfig config) => _config = config;
        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(_config);
        public Task SaveConfigAsync(AppConfig config) => Task.CompletedTask;
        public string GetConfigPath() => string.Empty;
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
        public Task<CommandAuditLog[]> GetLogsAsync(string? serverId = null, int limit = 100, string? keyword = null, bool includeHistory = false, int offset = 0, string? sessionId = null, string? tool = null, AuditCategory? category = null, IReadOnlyCollection<string>? userIds = null) => Task.FromResult(Array.Empty<CommandAuditLog>());
        public Task LogSqlAsync(SqlAuditLog log) => Task.CompletedTask;
        public Task<SqlAuditLog[]> GetSqlLogsAsync(string? dataSourceId = null, int limit = 100, string? keyword = null, bool includeHistory = false, int offset = 0, string? sessionId = null, string? tool = null, AuditCategory? category = null, IReadOnlyCollection<string>? userIds = null) => Task.FromResult(Array.Empty<SqlAuditLog>());
        public Task RecordSessionAsync(AuditSession session) => Task.CompletedTask;
        public Task<AuditSession[]> GetSessionsAsync(int limit = 50, string? ownerUserId = null) => Task.FromResult(Array.Empty<AuditSession>());
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
        public string? ServerUuid { get; set; }
        public Task<DatasourceTestResult> TestAsync(DataSourceConfig ds, CancellationToken ct = default) =>
            Task.FromResult(new DatasourceTestResult { Success = true });
        public Task<DatasourceQueryResult> QueryAsync(DataSourceConfig ds, string sql, int maxRows, CancellationToken ct = default)
        {
            if (sql.Contains("@@server_uuid", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new DatasourceQueryResult
                {
                    Success = true,
                    Columns = new[] { "@@server_uuid" },
                    Rows = new List<Dictionary<string, object?>>
                    {
                        new() { ["@@server_uuid"] = ServerUuid }
                    }
                });
            }

            return Task.FromResult(new DatasourceQueryResult
            {
                Success = true,
                Columns = new[] { "Host", "User" },
                Rows = new List<Dictionary<string, object?>>
                {
                    new() { ["Host"] = "127.0.0.1:50000", ["User"] = "app" }
                }
            });
        }
        public Task<DatasourceExecuteResult> ExecuteAsync(DataSourceConfig ds, string sql, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DatasourceQueryResult> ExplainAsync(DataSourceConfig ds, string sql, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DatasourceDiagnosticsResult> DiagnoseAsync(DataSourceConfig ds, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
