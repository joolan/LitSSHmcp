using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class SpillAndSubAgentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "litssh-spill-" + Guid.NewGuid().ToString("N"));

    private static IAgentTool Tool(IReadOnlyList<IAgentTool> tools, string name) => tools.First(t => t.Name == name);

    private static Dictionary<string, object?> Args(params (string, object?)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public async Task Spill_store_saves_resolves_and_blocks_escape()
    {
        var store = new SpillStore(_dir, 7);
        var content = string.Join('\n', Enumerable.Range(1, 500).Select(i => $"line{i}"));
        var handle = store.Save("ssh_execute_command", content);

        Assert.Contains("ssh_execute_command", handle);
        Assert.NotNull(store.Resolve(handle));
        Assert.NotNull(store.Resolve("spill://" + handle));
        Assert.Null(store.Resolve("../evil.txt"));
        Assert.Null(store.Resolve("a/b.txt"));

        var tools = SpillTools.Create(store);
        var read = await Tool(tools, "spill_read").InvokeAsync(Args(("handle", handle), ("offset", 100), ("limit", 5)), default);
        Assert.Contains("100: line101", (string)read!);
        Assert.Contains("共 500 行", (string)read!);

        var grep = await Tool(tools, "spill_grep").InvokeAsync(Args(("handle", handle), ("pattern", @"line12\d")), default);
        Assert.Contains("line120", (string)grep!);

        var list = await Tool(tools, "spill_list").InvokeAsync(Args(), default);
        Assert.Contains(handle, (string)list!);
    }

    [Fact]
    public async Task Large_tool_result_spills_to_handle_in_context()
    {
        var store = new SpillStore(_dir, 7);
        var call = new FunctionCallContent("c1", "echo", new Dictionary<string, object?>());
        var client = new FakeChatClient(
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, new AIContent[] { call }) }),
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "done") }));

        var big = new string('x', 1000);
        var tool = new FakeTool("echo", big);
        var config = new AgentConfig
        {
            ContextLimit = 0, ContextTokenLimit = 0, AutoSummarize = false,
            ToolResultMaxChars = 100, SpillLargeToolResults = true
        };
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new[] { tool }, "sys", config, null, null, store);

        await session.SendAsync("hi", null);

        var toolMessage = Assert.Single(session.Messages, m => m.Role == ChatRole.Tool);
        var text = Assert.IsType<FunctionResultContent>(Assert.Single(toolMessage.Contents)).Result!.ToString()!;
        Assert.Contains("已落盘为 spill://", text);
        Assert.True(text.Length < big.Length);
    }

    [Fact]
    public async Task Sub_agent_runs_isolated_and_returns_only_summary()
    {
        var client = new FakeChatClient(new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "sub-answer") }));
        var tool = SubAgents.Create(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new AgentConfig { MaxToolIterations = 3 }, Array.Empty<IAgentTool>());

        var result = await tool.InvokeAsync(Args(("task", "check server X")), default);

        Assert.Equal("sub-answer", result!.ToString());
        Assert.Equal("run_subagent", tool.Name);
    }

    [Fact]
    public void System_prompt_is_deterministic_for_cache_prefix()
    {
        var a = SystemPromptBuilder.Build("instr", "skill", "user", skillFiles: new[] { "references/a.md" });
        var b = SystemPromptBuilder.Build("instr", "skill", "user", skillFiles: new[] { "references/a.md" });
        Assert.Equal(a, b);
        Assert.StartsWith("你是 LitSSH 运维助手", a);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class FakeTool : IAgentTool
    {
        private readonly object? _result;
        public FakeTool(string name, object? result)
        {
            Name = name;
            _result = result;
            Tool = AIFunctionFactory.Create(() => _result, name);
        }
        public string Name { get; }
        public AITool Tool { get; }
        public bool Destructive => false;
        public Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct) => Task.FromResult(_result);
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses;
        public FakeChatClient(params ChatResponse[] responses) => _responses = new Queue<ChatResponse>(responses);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(_responses.Count > 0 ? _responses.Dequeue()
                : new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "ok") }));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = _responses.Count > 0 ? _responses.Dequeue()
                : new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "ok") });
            foreach (var message in response.Messages)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(message.Role, message.Contents);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
