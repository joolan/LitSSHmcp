using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.Tests;

/// <summary>ProxyJump（跳板机）工厂层校验：无效跳板在**连接前**即报错（不触网）。</summary>
public class ProxyJumpFactoryTests
{
    private static SshServerConfig Server(string id, string? jump = null) => new()
    {
        Id = id,
        Name = id,
        Host = "10.0.0.1",
        Port = 22,
        Username = "u",
        AuthType = AuthType.Password,
        Password = "p",
        ProxyJumpServerId = jump
    };

    [Fact]
    public void Unknown_jump_server_throws_before_connecting()
    {
        ServerConfigCache.Update(Array.Empty<SshServerConfig>());
        var server = Server("target", "missing");
        Assert.Throws<InvalidOperationException>(
            () => SshClientFactory.Create(server, knownHosts: null, hostKeyMode: SshHostKeyMode.Off));
    }

    [Fact]
    public void Self_jump_throws()
    {
        var server = Server("self", "self");
        ServerConfigCache.Update(new[] { server });
        Assert.Throws<InvalidOperationException>(
            () => SshClientFactory.Create(server, knownHosts: null, hostKeyMode: SshHostKeyMode.Off));
    }

    [Fact]
    public void Multi_level_jump_throws()
    {
        var jump = Server("jump", "another");
        var server = Server("target", "jump");
        ServerConfigCache.Update(new[] { jump, server });
        Assert.Throws<InvalidOperationException>(
            () => SshClientFactory.Create(server, knownHosts: null, hostKeyMode: SshHostKeyMode.Off));
    }

    [Fact]
    public void No_jump_returns_direct_client()
    {
        ServerConfigCache.Update(Array.Empty<SshServerConfig>());
        var server = Server("plain");
        using var client = SshClientFactory.Create(server, knownHosts: null, hostKeyMode: SshHostKeyMode.Off);
        Assert.Equal("10.0.0.1", client.ConnectionInfo.Host);
        Assert.Equal(22, client.ConnectionInfo.Port);
    }
}
