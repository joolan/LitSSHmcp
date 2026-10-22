using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

/// <summary>
/// 内置 MCP 子进程的宿主注入环境（评估报告 §10.A.5 / M3-4 §10.B.4）：
/// 归因 Id/会话目录/授权用户名只由宿主在此决定，缺省与省略行为必须稳定。
/// </summary>
public class McpToolHostEnvTests
{
    [Fact]
    public void Empty_inputs_yield_local_owner_without_extra_vars()
    {
        var env = McpToolHost.BuildBuiltinEnvironment(null, null, null);

        Assert.Equal("local", env[HostEnv.OwnerUserId]);
        Assert.Equal("1", env[HostEnv.InternalTools]);
        Assert.False(env.ContainsKey(HostEnv.SessionWorkspaceDir));
        Assert.False(env.ContainsKey(HostEnv.AuthzUser));
        Assert.False(env.ContainsKey(HostEnv.AuthzRole));
        // 执行模式属进程级（Web 宿主启动时设置），绝不进会话级字典
        Assert.False(env.ContainsKey(HostEnv.AuthzMode));
    }

    [Fact]
    public void Whitespace_inputs_are_treated_as_absent()
    {
        var env = McpToolHost.BuildBuiltinEnvironment("  ", "   ", "  ");

        Assert.Equal("local", env[HostEnv.OwnerUserId]);
        Assert.False(env.ContainsKey(HostEnv.SessionWorkspaceDir));
        Assert.False(env.ContainsKey(HostEnv.AuthzUser));
        Assert.False(env.ContainsKey(HostEnv.AuthzRole));
    }

    [Fact]
    public void Session_scoped_values_are_trimmed_and_mapped()
    {
        var env = McpToolHost.BuildBuiltinEnvironment(
            " 101 ", @" C:\jobs\101\s5 ", " alice ", " admin ");

        Assert.Equal("101", env[HostEnv.OwnerUserId]);
        Assert.Equal(@"C:\jobs\101\s5", env[HostEnv.SessionWorkspaceDir]);
        Assert.Equal("alice", env[HostEnv.AuthzUser]);
        Assert.Equal("admin", env[HostEnv.AuthzRole]);
    }
}
