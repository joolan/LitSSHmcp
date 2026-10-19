using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Topology;
using Xunit;

namespace LitSSHmcp.Tests;

/// <summary>P1 快照图谱：portmap/nginx_tls → domain/port/cert 节点与边。</summary>
public class SnapshotTopologyBuilderTests
{
    [Fact]
    public void Build_creates_domain_port_cert_nodes_and_edges()
    {
        var contribution = SnapshotTopologyBuilder.Build(Config(), Server(), Snapshot());

        Assert.Contains(contribution.Edges, e =>
            e.From == "port:s1:tcp-443" && e.To == "ssh:s1" && e.Type == "hostedOn");
        Assert.Contains(contribution.Edges, e =>
            e.From == "app:shop" && e.To == "port:s1:tcp-443" && e.Type == "listensOn");
        Assert.Contains(contribution.Edges, e =>
            e.From == "domain:shop.example.com" && e.To == "app:shop" && e.Type == "exposes");
        Assert.Contains(contribution.Edges, e =>
            e.From == "domain:shop.example.com" && e.To == "ssh:s1" && e.Type == "resolvesTo");
        Assert.Contains(contribution.Edges, e =>
            e.From == "domain:shop.example.com" && e.To.StartsWith("cert:", StringComparison.Ordinal) && e.Type == "tlsBy");

        Assert.Contains(contribution.NodeInfo.Keys, id => id.StartsWith("cert:", StringComparison.Ordinal));
        var certInfo = contribution.NodeInfo.First(kv => kv.Key.StartsWith("cert:", StringComparison.Ordinal)).Value;
        Assert.Equal("shop.example.com", certInfo["subjectCn"]?.ToString());
        Assert.Equal("/etc/ssl/shop.pem", certInfo["path"]?.ToString());
        Assert.Equal("2027-01-01T00:00:00Z", certInfo["expiresAt"]?.ToString());
    }

    [Fact]
    public void Build_links_datasource_and_skips_system_ports()
    {
        var contribution = SnapshotTopologyBuilder.Build(Config(), Server(), Snapshot());

        Assert.Contains(contribution.Edges, e =>
            e.From == "ds:db1" && e.To == "port:s1:tcp-3306" && e.Type == "listensOn");
        Assert.DoesNotContain(contribution.NodeInfo.Keys, id => id == "port:s1:tcp-22");
        Assert.DoesNotContain(contribution.Edges, e => e.From.Contains("sshd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_uses_snapshot_time_for_edge_freshness()
    {
        var contribution = SnapshotTopologyBuilder.Build(Config(), Server(), Snapshot());

        var edge = contribution.Edges.First(e => e.Type == "hostedOn");
        Assert.Equal(DateTime.Parse("2026-01-02T03:04:05Z").ToUniversalTime(), edge.LastSeenAt.ToUniversalTime());
        Assert.Equal(edge.FirstSeenAt, edge.LastSeenAt);
    }

    [Fact]
    public void Build_creates_pending_nodes_when_config_not_registered()
    {
        var config = new AppConfig
        {
            Servers = new[] { Server() }
        };

        var contribution = SnapshotTopologyBuilder.Build(config, Server(), Snapshot());

        Assert.Contains(contribution.Edges, e =>
            e.From == "app:disc:nginx" && e.To == "port:s1:tcp-443" && e.Type == "listensOn");
        Assert.Contains(contribution.Edges, e =>
            e.From == "ds:disc:mysql" && e.To == "port:s1:tcp-3306" && e.Type == "listensOn");
        Assert.Contains(contribution.Edges, e =>
            e.From == "domain:shop.example.com" && e.To == "app:disc:nginx" && e.Type == "exposes");
    }

    [Fact]
    public void Build_creates_service_container_and_deploy_nodes()
    {
        var config = Config();
        config.Applications = config.Applications.Append(new ApplicationConfig
        {
            Id = "web",
            Name = "shop-web",
            Type = "java",
            Port = 8080,
            ContainerName = "shop-web"
        }).ToArray();

        var snapshot = new ServerSnapshot
        {
            ServerId = "s1",
            ServerName = "web1",
            Status = SnapshotStatus.Succeeded,
            CreatedAt = "2026-01-02T03:04:05Z",
            CompletedAt = "2026-01-02T03:04:05Z",
            DataJson = """
            {
              "sections": {
                "systemd": {
                  "status": "ok",
                  "data": {
                    "systemState": "running",
                    "units": [
                      { "unit": "shop.service", "load": "loaded", "active": "active", "sub": "running", "description": "Shop service" },
                      { "unit": "cron.service", "load": "loaded", "active": "active", "sub": "running", "description": "Cron" }
                    ]
                  }
                },
                "docker": {
                  "status": "ok",
                  "data": {
                    "daemonRunning": true,
                    "containers": [
                      {
                        "id": "abc123",
                        "name": "shop-web",
                        "image": "registry.local/shop:1.4.2",
                        "state": "running",
                        "status": "Up 2 hours",
                        "ports": "0.0.0.0:8080->80/tcp, :::8080->80/tcp",
                        "createdAt": "2026-01-01T00:00:00Z"
                      }
                    ]
                  }
                },
                "portmap": {
                  "status": "ok",
                  "data": {
                    "listeners": [
                      { "proto": "tcp", "bind": "0.0.0.0", "port": 8080, "process": "java", "pid": 300, "unit": "shop.service" }
                    ]
                  }
                }
              }
            }
            """
        };

        var contribution = SnapshotTopologyBuilder.Build(config, Server(), snapshot);

        var serviceNode = "service:s1:shop";
        var containerNode = "container:s1:shop-web";
        var portNode = "port:s1:tcp-8080";
        Assert.True(contribution.NodeInfo.ContainsKey(serviceNode));
        Assert.True(contribution.NodeInfo.ContainsKey(containerNode));
        Assert.Contains(contribution.Edges, e =>
            e.From == serviceNode && e.To == "ssh:s1" && e.Type == "runsOn");
        Assert.Contains(contribution.Edges, e =>
            e.From == serviceNode && e.To == portNode && e.Type == "listensOn");
        Assert.Contains(contribution.Edges, e =>
            e.From == containerNode && e.To == "ssh:s1" && e.Type == "runsOn");
        Assert.Contains(contribution.Edges, e =>
            e.From == containerNode && e.To == portNode && e.Type == "listensOn");
        Assert.Contains(contribution.Edges, e =>
            e.From == "app:web" && e.To == "deploy:web@1.4.2" && e.Type == "deployedFrom");

        var deploy = contribution.NodeInfo["deploy:web@1.4.2"];
        Assert.Equal("1.4.2", deploy["version"]?.ToString());
        Assert.Equal("registry.local/shop:1.4.2", deploy["image"]?.ToString());
        Assert.Equal(0.9, contribution.Edges.First(e => e.Type == "hostedOn").Confidence);
        Assert.Equal(DateTime.Parse("2026-01-02T03:04:05Z").ToUniversalTime(),
            contribution.Edges.First(e => e.Type == "hostedOn").LastVerifiedAt?.ToUniversalTime());
    }

    [Fact]
    public void Build_notes_truncated_collectors()
    {
        var json = Snapshot().DataJson!
            .Replace("\"configTruncated\": false", "\"configTruncated\": true", StringComparison.Ordinal)
            .Replace("\"truncated\": false", "\"truncated\": true", StringComparison.Ordinal);

        var contribution = SnapshotTopologyBuilder.Build(Config(), Server(), new ServerSnapshot
        {
            ServerId = "s1",
            ServerName = "web1",
            Status = SnapshotStatus.Succeeded,
            CreatedAt = "2026-01-02T03:04:05Z",
            DataJson = json
        });

        Assert.Contains(contribution.Notes, n => n.Contains("端口快照已截断"));
        Assert.Contains(contribution.Notes, n => n.Contains("nginx 配置已截断"));
    }

    private static SshServerConfig Server() => new() { Id = "s1", Name = "web1", Host = "10.0.0.1" };

    private static AppConfig Config() => new()
    {
        Servers = new[] { Server() },
        Applications = new[]
        {
            new ApplicationConfig { Id = "shop", Name = "nginx", Type = "nginx", Port = 443 }
        },
        DataSources = new[]
        {
            new DataSourceConfig
            {
                Id = "db1",
                Name = "shop-db",
                Type = "mysql",
                Host = "127.0.0.1",
                Port = 3306,
                AccessMode = AccessMode.SshTunnel,
                TunnelServerId = "s1"
            }
        }
    };

    private static ServerSnapshot Snapshot() => new()
    {
        ServerId = "s1",
        ServerName = "web1",
        Status = SnapshotStatus.Succeeded,
        CreatedAt = "2026-01-02T03:04:05Z",
        CompletedAt = "2026-01-02T03:04:05Z",
        DataJson = """
        {
          "collectorVersion": 1,
          "sections": {
            "portmap": {
              "status": "ok",
              "data": {
                "truncated": false,
                "listeners": [
                  { "proto": "tcp", "bind": "0.0.0.0", "port": 443, "process": "nginx", "pid": 100, "user": "root", "anyBind": true },
                  { "proto": "tcp", "bind": "0.0.0.0", "port": 22, "process": "sshd", "pid": 1 },
                  { "proto": "tcp", "bind": "127.0.0.1", "port": 3306, "process": "mysqld", "pid": 200 }
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
                        "expired": false,
                        "dnsNames": ["shop.example.com", "www.shop.example.com"]
                      }
                    ]
                  }
                ]
              }
            }
          }
        }
        """
    };
}
