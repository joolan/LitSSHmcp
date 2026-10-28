using LitSSHmcp.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class MemoryRerankerTests
{
    [Fact]
    public async Task Reranks_hits_by_model_order_and_appends_rest()
    {
        var reranker = new MemoryReranker(new FakeChatClient("""{"order":[2,1]}"""));
        var hits = new List<MemoryHit>
        {
            new("s", "first", 0.9, Key: "a"),
            new("s", "second", 0.8, Key: "b"),
            new("s", "third", 0.7, Key: "c")
        };

        var result = await reranker.RerankAsync("query", hits);

        Assert.Equal(new[] { "b", "a", "c" }, result.Select(h => h.Key).ToArray());
    }

    [Fact]
    public async Task Invalid_model_output_keeps_original_order()
    {
        var reranker = new MemoryReranker(new FakeChatClient("not-json"));
        var hits = new List<MemoryHit>
        {
            new("s", "first", 0.9, Key: "a"),
            new("s", "second", 0.8, Key: "b")
        };

        var result = await reranker.RerankAsync("query", hits);

        Assert.Equal(new[] { "a", "b" }, result.Select(h => h.Key).ToArray());
    }

    private sealed class FakeChatClient(string json) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, json) }));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, new AIContent[] { new TextContent(json) });
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
