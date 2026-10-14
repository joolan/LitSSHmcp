using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class McpServerTests
{
    [Fact]
    public void Parse_pairs_handles_env_and_headers()
    {
        var env = McpToolHost.ParsePairs(new[] { "API_KEY=secret", "EMPTY=", "  SPACED = value ", "no-separator" }, '=');
        Assert.Equal("secret", env["API_KEY"]);
        Assert.Equal(string.Empty, env["EMPTY"]);
        Assert.Equal("value", env["SPACED"]);
        Assert.False(env.ContainsKey("no-separator"));

        var headers = McpToolHost.ParsePairs(new[] { "Authorization: Bearer t", "X-Url: https://a/b" }, ':');
        Assert.Equal("Bearer t", headers["Authorization"]);
        Assert.Equal("https://a/b", headers["X-Url"]);
    }

    [Fact]
    public void Prompt_lists_external_servers_with_security_boundary()
    {
        var prompt = SystemPromptBuilder.Build(null, null, null,
            externalMcp: new[]
            {
                new ExternalMcpServerInfo("飞书", 12, new[] { "`fs_send` → 原名 `send_message`" }, null),
                new ExternalMcpServerInfo("坏服务器", 0, Array.Empty<string>(), "连接被拒绝"),
            });

        Assert.Contains("## 外部 MCP 服务器", prompt);
        Assert.Contains("飞书 | 12 个工具 | 正常", prompt);
        Assert.Contains("`fs_send` → 原名 `send_message`", prompt);
        Assert.Contains("**连接失败**: 连接被拒绝", prompt);
        Assert.Contains("不经过本程序的人工审批/命令过滤/审计", prompt);
    }

    [Fact]
    public void Prompt_notes_builtin_mcp_disabled()
    {
        var prompt = SystemPromptBuilder.Build(null, null, null, builtinMcpDisabled: true);

        Assert.Contains("内置 LitSSH MCP **已停用**", prompt);
        Assert.Contains("## 外部 MCP 服务器", prompt);
    }

    [Fact]
    public void Prompt_omits_external_section_when_not_applicable()
    {
        var prompt = SystemPromptBuilder.Build(null, null, null);
        Assert.DoesNotContain("## 外部 MCP 服务器", prompt);
        // 全部外部服务器连接失败时，不渲染第三方安全提示（避免误导）
        var allFailed = SystemPromptBuilder.Build(null, null, null,
            externalMcp: new[] { new ExternalMcpServerInfo("坏服务器", 0, Array.Empty<string>(), "x") });
        Assert.DoesNotContain("不经过本程序的人工审批/命令过滤/审计", allFailed);
    }

    [Theory]
    [InlineData("http", "")]
    [InlineData("http", "not-a-url")]
    [InlineData("stdio", "")]
    public async Task Validator_reports_configuration_errors_without_connecting(string transport, string value)
    {
        var config = new AgentMcpServerConfig
        {
            Id = "ext1", Name = "ext", Transport = transport,
            Command = transport == "stdio" ? value : string.Empty,
            Url = transport == "http" ? value : string.Empty,
        };

        var result = await McpServerValidator.ValidateAsync(config);

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal(0, result.ToolCount);
    }

    [Fact]
    public void Normalize_keeps_single_builtin_entry_first()
    {
        var external = new AgentMcpServerConfig { Id = "ext1", Name = "ext" };
        var builtin = AgentMcpServerConfig.BuiltinDefault();

        var fromEmpty = AgentMcpServerConfig.Normalize(null);
        Assert.Single(fromEmpty);
        Assert.True(fromEmpty[0].Builtin);

        var reordered = AgentMcpServerConfig.Normalize(new[] { external, builtin });
        Assert.Equal(2, reordered.Length);
        Assert.True(reordered[0].Builtin);
        Assert.Equal("ext1", reordered[1].Id);

        var missingBuiltin = AgentMcpServerConfig.Normalize(new[] { external });
        Assert.Equal(2, missingBuiltin.Length);
        Assert.True(missingBuiltin[0].Builtin);
        Assert.Equal("ext1", missingBuiltin[1].Id);
    }
}
