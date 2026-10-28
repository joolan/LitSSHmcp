using System.Runtime.CompilerServices;
using LitSSHmcp.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class ResilientChatClientTests
{
    [Fact]
    public async Task Shared_gate_serializes_wrappers_for_same_provider()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var probe = new ConcurrencyProbeChatClient();
        var a = new ResilientChatClient(probe, 1, 30, 0, gate);
        var b = new ResilientChatClient(probe, 1, 30, 0, gate);

        var first = a.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "1") });
        await Task.Delay(50);
        var second = b.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "2") });
        await Task.WhenAll(first, second);

        Assert.Equal(1, probe.MaxConcurrency);
    }

    [Fact]
    public async Task Own_gate_still_limits_single_wrapper()
    {
        var probe = new ConcurrencyProbeChatClient();
        var client = new ResilientChatClient(probe, 1, 30, 0);

        await Task.WhenAll(
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "1") }),
            client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "2") }));

        Assert.Equal(1, probe.MaxConcurrency);
    }

    private sealed class ConcurrencyProbeChatClient : IChatClient
    {
        private readonly object _sync = new();
        private int _current;

        public int MaxConcurrency { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _current++;
                MaxConcurrency = Math.Max(MaxConcurrency, _current);
            }

            try
            {
                await Task.Delay(100, cancellationToken);
                return new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "ok") });
            }
            finally
            {
                lock (_sync)
                    _current--;
            }
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
