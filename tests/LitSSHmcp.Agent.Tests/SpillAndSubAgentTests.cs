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
    public void Spill_store_isolates_owners()
    {
        var alice = new SpillStore(_dir, 7, "1");
        var bob = new SpillStore(_dir, 7, "2");
        var handle = alice.Save("ssh_execute_command", "alice-secret");

        Assert.NotNull(alice.Resolve(handle));
        Assert.Null(bob.Resolve(handle));
        Assert.Empty(bob.List());
        Assert.Contains(handle, alice.List());
        Assert.True(File.Exists(Path.Combine(alice.Dir, handle)));
        Assert.False(File.Exists(Path.Combine(bob.Dir, handle)));

        var samplesDir = Path.Combine(_dir, "samples");
        var samples = new SpillStore(samplesDir, 7, "..\\..\\alice");
        Assert.StartsWith(Path.GetFullPath(samplesDir), Path.GetFullPath(samples.Dir), StringComparison.OrdinalIgnoreCase);
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
    public async Task Sub_agent_inherits_tool_policy_and_does_not_run_denied_tool()
    {
        var denied = new FakeTool("danger", "ran");
        var call = new FunctionCallContent("c1", "danger", new Dictionary<string, object?>());
        var client = new FakeChatClient(
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, new AIContent[] { call }) }),
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "done") }));
        var tool = SubAgents.Create(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new AgentConfig { MaxToolIterations = 3 }, new IAgentTool[] { denied },
            toolPolicy: new Dictionary<string, AgentToolPolicy> { ["danger"] = AgentToolPolicy.Deny });

        var result = await tool.InvokeAsync(Args(("task", "check server X")), default);

        Assert.Equal("done", result!.ToString());
        Assert.Equal(0, denied.InvokeCount);
    }

    [Fact]
    public async Task Sub_agent_inherits_plan_mode_and_blocks_write_tools()
    {
        var write = new FakeTool("write_tool", "ran");
        var call = new FunctionCallContent("c1", "write_tool", new Dictionary<string, object?>());
        var client = new FakeChatClient(
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, new AIContent[] { call }) }),
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "done") }));
        var tool = SubAgents.Create(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new AgentConfig { MaxToolIterations = 3 }, new IAgentTool[] { write }, planMode: true);

        var result = await tool.InvokeAsync(Args(("task", "check server X")), default);

        Assert.Equal("done", result!.ToString());
        Assert.Equal(0, write.InvokeCount);
    }

    [Fact]
    public async Task Sub_agent_reads_live_tool_policy_provider()
    {
        var denied = new FakeTool("danger", "ran");
        var call = new FunctionCallContent("c1", "danger", new Dictionary<string, object?>());
        var client = new FakeChatClient(
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, new AIContent[] { call }) }),
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "done") }));
        var policy = new Dictionary<string, AgentToolPolicy>();
        var tool = SubAgents.Create(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new AgentConfig { MaxToolIterations = 3 }, new IAgentTool[] { denied },
            toolPolicyProvider: () => policy);

        policy["danger"] = AgentToolPolicy.Deny;
        var result = await tool.InvokeAsync(Args(("task", "check server X")), default);

        Assert.Equal("done", result!.ToString());
        Assert.Equal(0, denied.InvokeCount);
    }

    [Fact]
    public async Task Sub_agent_reads_live_plan_mode_provider()
    {
        var write = new FakeTool("write_tool", "ran");
        var call = new FunctionCallContent("c1", "write_tool", new Dictionary<string, object?>());
        var client = new FakeChatClient(
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, new AIContent[] { call }) }),
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "done") }));
        var planMode = false;
        var tool = SubAgents.Create(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new AgentConfig { MaxToolIterations = 3 }, new IAgentTool[] { write },
            planModeProvider: () => planMode);

        planMode = true;
        var result = await tool.InvokeAsync(Args(("task", "check server X")), default);

        Assert.Equal("done", result!.ToString());
        Assert.Equal(0, write.InvokeCount);
    }

    [Fact]
    public async Task Sub_agent_reports_usage_back_to_parent()
    {
        var usage = new UsageDetails { InputTokenCount = 120, OutputTokenCount = 30 };
        var client = new FakeChatClient(usage,
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "sub-answer") }));
        long input = 0, output = 0;
        var tool = SubAgents.Create(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new AgentConfig { MaxToolIterations = 3 }, Array.Empty<IAgentTool>(),
            onUsage: (i, o) => { input = i; output = o; });

        await tool.InvokeAsync(Args(("task", "check server X")), default);

        Assert.Equal(120L, input);
        Assert.Equal(30L, output);
    }

    [Fact]
    public async Task Sub_agent_stops_when_budget_is_exhausted_before_tool_call()
    {
        var write = new FakeTool("danger", "ran");
        var call = new FunctionCallContent("c1", "danger", new Dictionary<string, object?>());
        var usage = new UsageDetails { InputTokenCount = 1_000_000, OutputTokenCount = 1_000_000 };
        var client = new FakeChatClient(usage,
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, new AIContent[] { call }) }),
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "should-not-run") }));
        var checks = 0;
        var tool = SubAgents.Create(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new AgentConfig { MaxToolIterations = 3 }, new IAgentTool[] { write },
            budgetCheck: (_, _) => ++checks == 1);

        var result = await tool.InvokeAsync(Args(("task", "check server X")), default);

        Assert.Equal(0, write.InvokeCount);
        Assert.True(checks >= 2);
        Assert.Contains("预算", result!.ToString());
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
        public bool ReadOnly => false;
        public string? ToolGroup => null;
        public int InvokeCount { get; private set; }
        public Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
        {
            InvokeCount++;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses;
        private readonly UsageDetails? _usage;

        public FakeChatClient(params ChatResponse[] responses) => _responses = new Queue<ChatResponse>(responses);

        public FakeChatClient(UsageDetails usage, params ChatResponse[] responses)
        {
            _usage = usage;
            _responses = new Queue<ChatResponse>(responses);
        }

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
            if (_usage is not null)
                yield return new ChatResponseUpdate(ChatRole.Assistant, new AIContent[] { new UsageContent(_usage) });
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
