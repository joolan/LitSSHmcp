using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class McpBuiltinHostPoolTests
{
    [Fact]
    public void Builtin_host_key_normalizes_identity_and_workspace()
    {
        var launch = new McpServerLaunch("server.exe", new[] { "--flag", "value" }, "bin");
        var key = McpBuiltinHostPool.CreateKey(launch, "  ", null, null, null, 4);

        Assert.Equal("server.exe", key.LaunchFile);
        Assert.Equal("--flag\u001fvalue", key.LaunchArguments);
        Assert.Equal("bin", key.WorkingDirectory);
        Assert.Equal("local", key.OwnerUserId);
        Assert.Null(key.SessionWorkspaceDir);
        Assert.Null(key.OwnerUsername);
        Assert.Null(key.OwnerRole);
        Assert.Equal(4, key.ConfigGeneration);

        var dir = Path.Combine(Path.GetTempPath(), "litssh-host-key");
        var withDir = McpBuiltinHostPool.CreateKey(launch, "alice", dir, " Alice ", "admin", -2);
        Assert.Equal("alice", withDir.OwnerUserId);
        Assert.Equal(Path.GetFullPath(dir), withDir.SessionWorkspaceDir);
        Assert.Equal("Alice", withDir.OwnerUsername);
        Assert.Equal("admin", withDir.OwnerRole);
        Assert.Equal(0, withDir.ConfigGeneration);
    }
}
