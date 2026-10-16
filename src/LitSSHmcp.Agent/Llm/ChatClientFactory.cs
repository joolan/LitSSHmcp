using System.ClientModel;
using System.Security.Cryptography;
using System.Text;
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
        var resilient = new ResilientChatClient(raw, provider.MaxConcurrency, provider.TimeoutSeconds, provider.MaxRetries);
        resilient.PromptCacheKey = ComputePromptCacheKey(provider.Endpoint, provider.Id, provider.Model);
        return resilient;
    }

    /// <summary>
    /// 计算 <c>prompt_cache_key</c>（OpenAI 官方顶层字段，用于前缀缓存分组路由）：
    /// 仅对官方端点生成（其他 OpenAI 兼容端点可能拒绝未知字段导致请求失败），返回 null=不注入。
    /// 值 = providerId+model 的稳定短哈希——跨会话/重启不变，变化会打散缓存分组。
    /// </summary>
    public static string? ComputePromptCacheKey(string? endpoint, string providerId, string model)
    {
        if (!IsOfficialOpenAIEndpoint(endpoint))
            return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{providerId}|{model}"));
        return "litssh-" + Convert.ToHexString(hash).Substring(0, 16).ToLowerInvariant();
    }

    /// <summary>是否 OpenAI 官方端点（含子域）；endpoint 为空 = SDK 默认 api.openai.com。</summary>
    public static bool IsOfficialOpenAIEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return true;
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
            return false;
        return uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase);
    }
}
