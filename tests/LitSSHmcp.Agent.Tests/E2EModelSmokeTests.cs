using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

/// <summary>
/// 真实大模型端到端冒烟（**默认跳过**）：仅当设置 <c>LITSSH_AGENT_E2E=1</c> 且提供
/// <c>LITSSH_AGENT_E2E_KEY</c>（可选 endpoint/model）时才真正发起请求。
/// </summary>
public class E2EModelSmokeTests
{
    [Fact]
    public async Task Chat_completion_smoke_when_enabled()
    {
        if (Environment.GetEnvironmentVariable("LITSSH_AGENT_E2E") != "1")
            return;   // 未开启：跳过（视为通过）

        var key = Environment.GetEnvironmentVariable("LITSSH_AGENT_E2E_KEY");
        Assert.False(string.IsNullOrWhiteSpace(key), "已启用 E2E 但未设置 LITSSH_AGENT_E2E_KEY");

        var provider = new AgentProviderConfig
        {
            Type = "openai",
            Endpoint = Environment.GetEnvironmentVariable("LITSSH_AGENT_E2E_ENDPOINT") ?? "https://api.openai.com/v1",
            Model = Environment.GetEnvironmentVariable("LITSSH_AGENT_E2E_MODEL") ?? "gpt-4o-mini",
            ApiKey = key,
            TimeoutSeconds = 60,
            MaxRetries = 0
        };

        var client = ChatClientFactory.Create(provider);
        var response = await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "只回复两个字: 收到") },
            new ChatOptions { MaxOutputTokens = 16, Temperature = 0 });

        Assert.False(string.IsNullOrWhiteSpace(response.Text));
    }
}
