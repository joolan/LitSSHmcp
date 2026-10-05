using System.ClientModel;
using LitSSHmcp.Core.Models;
using Microsoft.Extensions.AI;
using OpenAI;

namespace LitSSHmcp.Agent;

/// <summary>按配置构建 embeddings 生成器（OpenAI 兼容 embeddings 端点）。</summary>
public static class EmbeddingClientFactory
{
    public static IEmbeddingGenerator<string, Embedding<float>> Create(AgentMemoryConfig config, string? fallbackApiKey)
    {
        var apiKey = string.IsNullOrWhiteSpace(config.ApiKey) ? fallbackApiKey : config.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("未配置 embeddings 的 API Key（可在设置里单独填，或复用对话模型的 Key）");
        if (string.IsNullOrWhiteSpace(config.Model))
            throw new InvalidOperationException("未配置 embeddings 模型名（如 text-embedding-3-small / bge-m3）");

        var options = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(config.Endpoint))
            options.Endpoint = new Uri(config.Endpoint.Trim());

        var client = new OpenAIClient(new ApiKeyCredential(apiKey!), options);
        return client.GetEmbeddingClient(config.Model.Trim()).AsIEmbeddingGenerator();
    }
}
