using System.ClientModel;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LitSSHmcp.Core.Models;
using Microsoft.Extensions.AI;
using OpenAI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 按 provider 配置构建 <see cref="IChatClient"/>（type=openai → OpenAI 兼容端点）。
/// 底层 <see cref="OpenAIClient"/> 按「端点+API Key+模型」缓存复用：每次新建且从不 Dispose 会泄漏连接句柄，
/// 并行会话（每会话一个 runtime）会把泄漏放大到每会话一次（评估报告 §4.2）。
/// </summary>
public static class ChatClientFactory
{
    /// <summary>底层客户端缓存（进程级共享；IChatClient 线程安全，可跨会话复用）。</summary>
    private static readonly ConcurrentDictionary<string, Lazy<IChatClient>> RawClients = new(StringComparer.Ordinal);

    public static IChatClient Create(AgentProviderConfig provider)
    {
        if (!provider.Type.Equals("openai", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"暂不支持的模型类型: {provider.Type}（当前仅支持 openai 兼容端点）");
        if (string.IsNullOrWhiteSpace(provider.Model))
            throw new InvalidOperationException("未配置模型名(model)");
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidOperationException("未配置 API Key");

        var raw = GetOrCreateRawClient(provider);
        // 手动驱动工具循环(见 AgentSession)，因此不启用自动函数调用中间件；但加并发/超时/重试中间件。
        var resilient = new ResilientChatClient(raw, provider.MaxConcurrency, provider.TimeoutSeconds, provider.MaxRetries);
        resilient.PromptCacheKey = ComputePromptCacheKey(provider.Endpoint, provider.Id, provider.Model);
        return resilient;
    }

    /// <summary>取（或建）缓存中的底层客户端；并发首建时只有写进字典的那份被使用，其余丢弃。</summary>
    private static IChatClient GetOrCreateRawClient(AgentProviderConfig provider)
    {
        var model = provider.Model.Trim();
        var endpoint = provider.Endpoint?.Trim();
        var key = $"{endpoint}|{HashSecret(provider.ApiKey!)}|{model}";

        return RawClients.GetOrAdd(key, _ => new Lazy<IChatClient>(() =>
        {
            var options = new OpenAIClientOptions();
            if (!string.IsNullOrWhiteSpace(endpoint))
                options.Endpoint = new Uri(endpoint!);
            var client = new OpenAIClient(new ApiKeyCredential(provider.ApiKey!), options);
            return client.GetChatClient(model).AsIChatClient();
        })).Value;
    }

    /// <summary>缓存键只留密钥哈希，避免 API Key 以明文形式出现在内存字典键中。</summary>
    private static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

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
