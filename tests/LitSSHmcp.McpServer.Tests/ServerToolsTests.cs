using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Tools;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

public class ServerToolsTests
{
    [Fact]
    public async Task List_servers_exposes_configured_internal_address_without_hiding_public_host()
    {
        var tools = new ServerTools(new StubConfig(new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig
                {
                    Id = "web01",
                    Name = "web-01",
                    Host = "203.0.113.10",
                    Port = 22,
                    InternalHost = "10.0.0.15",
                    InternalPort = 2222,
                    ProxyJumpServerId = "jump01",
                    Username = "root"
                }
            }
        }), sshService: null!, auditLogService: null!);

        var result = await tools.ListServers();

        var server = Assert.Single(result.Servers);
        Assert.Equal("203.0.113.10", server.Host);
        Assert.Equal("10.0.0.15", server.InternalHost);
        Assert.Equal(2222, server.InternalPort);
        Assert.Equal("jump01", server.ProxyJumpServerId);
    }

    [Fact]
    public async Task List_servers_keeps_internal_address_null_when_not_configured()
    {
        var tools = new ServerTools(new StubConfig(new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "db01", Host = "198.51.100.20", Port = 22 }
            }
        }), sshService: null!, auditLogService: null!);

        var server = Assert.Single((await tools.ListServers()).Servers);

        Assert.Null(server.InternalHost);
        Assert.Null(server.InternalPort);
        Assert.Null(server.ProxyJumpServerId);
    }

    private sealed class StubConfig(AppConfig config) : IConfigService
    {
        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(config);
        public Task SaveConfigAsync(AppConfig value) => Task.CompletedTask;
        public string GetConfigPath() => "config.json";
    }
}
