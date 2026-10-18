using LitSSHmcp.Core.Models;
using LitSSHmcp.Web.Sessions;
using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>
/// Web 连接器下发白名单（评估报告 §10.B.7 攻击面收敛 / M3-4）：
/// 外部 MCP 默认不下发 Web，仅 security.authorization.webConnectors 授权的 Id 随会话启用。
/// </summary>
public class WebConnectorSubsetTests
{
    [Fact]
    public void Default_whitelist_yields_builtin_only()
    {
        var ids = AgentWebRuntimeFactory.ComputeWebEnabledMcpIds(new AuthorizationConfig());

        Assert.Equal(new[] { "litssh" }, ids.OrderBy(i => i, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Whitelisted_connectors_are_included_and_trimmed()
    {
        var authorization = new AuthorizationConfig
        {
            WebConnectors = new[] { " ext-a ", "ext-b", "", "litssh" }
        };

        var ids = AgentWebRuntimeFactory.ComputeWebEnabledMcpIds(authorization);

        Assert.Equal(
            new[] { "ext-a", "ext-b", "litssh" },
            ids.OrderBy(i => i, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Whitelist_does_not_confer_globally_disabled_connectors()
    {
        // 白名单只是"允许下发"；最终生效集合仍由 McpSubsetPolicy 与全局 Enabled 取交（D8 配置层优先）。
        // 这里验证传给 StartAsync 的子集只含白名单内 id，不含未授权的外部连接器。
        var authorization = new AuthorizationConfig
        {
            WebConnectors = new[] { "ext-allowed" }
        };
        var servers = new[]
        {
            AgentMcpServerConfig.BuiltinDefault(),
            new AgentMcpServerConfig { Id = "ext-allowed", Name = "A", Enabled = true },
            new AgentMcpServerConfig { Id = "ext-forbidden", Name = "F", Enabled = true }
        };

        var ids = AgentWebRuntimeFactory.ComputeWebEnabledMcpIds(authorization);
        var effective = LitSSHmcp.Agent.McpSubsetPolicy.EffectiveServers(servers, ids);

        Assert.Equal(new[] { "litssh", "ext-allowed" }, effective.Select(s => s.Id).ToArray());
        Assert.DoesNotContain(effective, s => s.Id == "ext-forbidden");
    }

    [Fact]
    public void Null_web_connectors_array_is_safe()
    {
        var authorization = new AuthorizationConfig { WebConnectors = null! };

        var ids = AgentWebRuntimeFactory.ComputeWebEnabledMcpIds(authorization);

        Assert.Contains("litssh", ids);
        Assert.Single(ids);
    }
}
