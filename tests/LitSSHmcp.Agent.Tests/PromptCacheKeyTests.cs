using System.ClientModel.Primitives;
using LitSSHmcp.Agent;
using Microsoft.Extensions.AI;
using Xunit;
using OpenAiChat = OpenAI.Chat.ChatCompletionOptions;

namespace LitSSHmcp.Agent.Tests;

public class PromptCacheKeyTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1", true)]
    [InlineData("https://azure.openai.com/v1", true)]
    [InlineData("https://api.deepseek.com/v1", false)]
    [InlineData("http://127.0.0.1:11434/v1", false)]
    [InlineData("", true)]      // 未填 endpoint = SDK 默认 api.openai.com
    [InlineData(null, true)]
    public void IsOfficialOpenAIEndpoint_gates_by_host(string? endpoint, bool expected) =>
        Assert.Equal(expected, ChatClientFactory.IsOfficialOpenAIEndpoint(endpoint));

    [Fact]
    public void ComputePromptCacheKey_official_endpoint_is_stable_and_prefixed()
    {
        var a = ChatClientFactory.ComputePromptCacheKey("https://api.openai.com/v1", "p1", "gpt-x");
        var b = ChatClientFactory.ComputePromptCacheKey("https://api.openai.com/v1", "p1", "gpt-x");
        Assert.NotNull(a);
        Assert.Equal(a, b);                       // 跨调用稳定（缓存分组不被打散）
        Assert.StartsWith("litssh-", a);
    }

    [Fact]
    public void ComputePromptCacheKey_non_official_returns_null() =>
        Assert.Null(ChatClientFactory.ComputePromptCacheKey("https://api.deepseek.com/v1", "p1", "deepseek-chat"));

    [Fact]
    public async Task GetResponseAsync_injects_cache_key_into_options()
    {
        var client = new ResilientChatClient(new EchoChatClient(), 2, 30, 0)
        {
            PromptCacheKey = "litssh-test"
        };
        var options = new ChatOptions();

        await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, options);

        Assert.NotNull(options.RawRepresentationFactory);
        var raw = Assert.IsType<OpenAiChat>(options.RawRepresentationFactory!(new EchoChatClient()));
        Assert.True(raw.Patch.Contains("$.prompt_cache_key"u8));
    }

    [Fact]
    public async Task GetResponseAsync_does_not_override_caller_factory()
    {
        var client = new ResilientChatClient(new EchoChatClient(), 2, 30, 0)
        {
            PromptCacheKey = "litssh-test"
        };
        var callerInvoked = false;
        var options = new ChatOptions
        {
            RawRepresentationFactory = _ =>
            {
                callerInvoked = true;
                return new OpenAiChat();
            }
        };

        await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, options);

        Assert.False(callerInvoked);              // 本次调用不触发调用方 factory（仅断言未被替换，不执行它）
        Assert.NotNull(options.RawRepresentationFactory);
    }

    [Fact]
    public async Task GetResponseAsync_without_key_leaves_options_untouched()
    {
        var client = new ResilientChatClient(new EchoChatClient(), 2, 30, 0);   // PromptCacheKey=null
        var options = new ChatOptions();

        await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, options);

        Assert.Null(options.RawRepresentationFactory);
    }

    private sealed class EchoChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
