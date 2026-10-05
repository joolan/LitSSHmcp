using System.ClientModel;
using LitSSHmcp.Core.Models;
using Microsoft.Extensions.AI;
using OpenAI;

namespace LitSSHmcp.Agent;

/// <summary>按 provider 配置构建 <see cref="IChatClient"/>（type=openai → OpenAI 兼容端点）。</summary>
public static class ChatClientFactory
{
    public static IChatClient Create(AgentProviderConfig provider)
    {
        if (!provider.Type.Equals("openai", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"暂不支持的模型类型: {provider.Type}（当前仅支持 openai 兼容端点）");
        if (string.IsNullOrWhiteSpace(provider.Model))
            throw new InvalidOperationException("未配置模型名(model)");
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidOperationException("未配置 API Key");

        var options = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(provider.Endpoint))
            options.Endpoint = new Uri(provider.Endpoint.Trim());

        var client = new OpenAIClient(new ApiKeyCredential(provider.ApiKey!), options);
        // 手动驱动工具循环(见 AgentSession)，因此不启用自动函数调用中间件；但加并发/超时/重试中间件。
        IChatClient raw = client.GetChatClient(provider.Model.Trim()).AsIChatClient();
        return new ResilientChatClient(raw, provider.MaxConcurrency, provider.TimeoutSeconds, provider.MaxRetries);
    }
}
