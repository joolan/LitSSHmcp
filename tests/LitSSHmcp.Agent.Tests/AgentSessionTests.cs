using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class AgentSessionTests
{
    [Fact]
    public async Task Tool_call_loop_invokes_tool_and_returns_final_text()
    {
        // 第 1 个响应：模型请求调用 echo 工具；第 2 个响应：最终文本。
        var call = new FunctionCallContent("c1", "echo", new Dictionary<string, object?> { ["x"] = "y" });
        var assistantWithCall = new ChatMessage(ChatRole.Assistant, new AIContent[] { call });
        var final = new ChatMessage(ChatRole.Assistant, "done");

        var client = new FakeChatClient(
            new ChatResponse(new List<ChatMessage> { assistantWithCall }),
            new ChatResponse(new List<ChatMessage> { final }));

        var tool = new FakeTool("echo", "pong");
        var provider = new AgentProviderConfig { Model = "m", ApiKey = "k", Temperature = 0.2 };
        var config = new AgentConfig { MaxToolIterations = 5, ContextLimit = 50 };
        var session = new AgentSession(client, provider, new[] { tool }, "sys", config);

        var events = new List<AgentEvent>();
        await session.SendAsync("hi", new SyncProgress(events.Add));

        // 工具被调用且收到了参数
        Assert.NotNull(tool.LastArgs);
        Assert.Equal("y", tool.LastArgs!["x"]);

        // 上下文里应包含工具结果消息
        var toolMessage = Assert.Single(session.Messages, m => m.Role == ChatRole.Tool);
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(toolMessage.Contents));
        Assert.Equal("pong", result.Result);

        // 事件序列
        Assert.Contains(events, e => e.Kind == AgentEventKind.ToolCall && e.ToolName == "echo");
        Assert.Contains(events, e => e.Kind == AgentEventKind.ToolResult && e.Text == "pong");
        Assert.Contains(events, e => e.Kind == AgentEventKind.AssistantText && e.Text == "done");
        Assert.Contains(events, e => e.Kind == AgentEventKind.Done);
    }

    [Fact]
    public async Task Unknown_tool_reports_error_result()
    {
        var call = new FunctionCallContent("c1", "nope", new Dictionary<string, object?>());
        var assistantWithCall = new ChatMessage(ChatRole.Assistant, new AIContent[] { call });
        var final = new ChatMessage(ChatRole.Assistant, "ok");
        var client = new FakeChatClient(
            new ChatResponse(new List<ChatMessage> { assistantWithCall }),
            new ChatResponse(new List<ChatMessage> { final }));

        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            Array.Empty<IAgentTool>(), "sys", new AgentConfig());

        var events = new List<AgentEvent>();
        await session.SendAsync("hi", new SyncProgress(events.Add));

        Assert.Contains(events, e => e.Kind == AgentEventKind.ToolResult && !e.ToolSuccess);
    }

    [Fact]
    public async Task InvokeTool_retries_without_model()
    {
        var tool = new FakeTool("echo", "pong");
        var session = new AgentSession(new FakeChatClient(),
            new AgentProviderConfig { Model = "m", ApiKey = "k" }, new[] { tool }, "sys", new AgentConfig());

        var (ok, result, _) = await session.InvokeToolAsync("echo", new Dictionary<string, object?> { ["x"] = "y" }, default);

        Assert.True(ok);
        Assert.Equal("pong", result);
        Assert.Equal("y", tool.LastArgs!["x"]);
    }

    [Fact]
    public void ReplaceHistory_keeps_system_prompt()
    {
        var session = new AgentSession(new FakeChatClient(),
            new AgentProviderConfig { Model = "m", ApiKey = "k" }, Array.Empty<IAgentTool>(), "SYS", new AgentConfig());

        session.ReplaceHistory(new[]
        {
            new ChatMessage(ChatRole.User, "u"),
            new ChatMessage(ChatRole.Assistant, "a")
        });

        Assert.Equal(3, session.Messages.Count);
        Assert.Equal("SYS", session.Messages[0].Text);
        Assert.True(session.EstimatedTokens >= 0);
    }

    [Fact]
    public async Task Drops_whole_oldest_turns_and_keeps_tool_pairing()
    {
        var call = new FunctionCallContent("c1", "echo", new Dictionary<string, object?>());
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "u1"),
            new(ChatRole.Assistant, new AIContent[] { call }),
            new(ChatRole.Tool, new AIContent[] { new FunctionResultContent("c1", "r1") }),
            new(ChatRole.Assistant, "a1"),
            new(ChatRole.User, "u2"),
            new(ChatRole.Assistant, "a2"),
            new(ChatRole.User, "u3"),
            new(ChatRole.Assistant, "a3"),
        };

        var client = new FakeChatClient(new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "final") }));
        var config = new AgentConfig { ContextLimit = 3, ContextTokenLimit = 0, AutoSummarize = false };
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            Array.Empty<IAgentTool>(), "SYS", config, history);

        await session.SendAsync("hi", null);

        Assert.Equal("SYS", session.Messages[0].Text);
        Assert.DoesNotContain(session.Messages, m => m.Text == "a1");   // 最旧的整轮被裁掉
        Assert.DoesNotContain(session.Messages, m => m.Text == "a2");
        Assert.Contains(session.Messages, m => m.Text == "a3");         // 最近轮保留

        // 配对不变量：任何 Tool 消息前方必须有匹配 CallId 的 assistant function-call
        for (var i = 0; i < session.Messages.Count; i++)
        {
            if (session.Messages[i].Role != ChatRole.Tool)
                continue;
            var callId = Assert.IsType<FunctionResultContent>(session.Messages[i].Contents[0]).CallId;
            var paired = session.Messages.Take(i)
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .Any(c => c.CallId == callId);
            Assert.True(paired, $"Tool 消息 {callId} 缺少配对的 assistant 调用");
        }
    }

    [Fact]
    public async Task Summarizes_dropped_turns_when_enabled()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "u1"),
            new(ChatRole.Assistant, "a1"),
            new(ChatRole.User, "u2"),
            new(ChatRole.Assistant, "a2"),
            new(ChatRole.User, "u3"),
            new(ChatRole.Assistant, "a3"),
        };

        var client = new FakeChatClient(
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "SUMMARY") }),
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "final") }));
        var config = new AgentConfig { ContextLimit = 3, ContextTokenLimit = 0, AutoSummarize = true };
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            Array.Empty<IAgentTool>(), "SYS", config, history);

        await session.SendAsync("hi", null);

        Assert.DoesNotContain(session.Messages, m => m.Text == "a1");
        Assert.Contains(session.Messages, m => m.Role == ChatRole.System && m.Text!.Contains("SUMMARY"));
        Assert.Contains(session.Messages, m => m.Text == "a3");
    }

    [Fact]
    public async Task Truncates_large_tool_result_in_context_but_not_ui()
    {
        var call = new FunctionCallContent("c1", "echo", new Dictionary<string, object?>());
        var client = new FakeChatClient(
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, new AIContent[] { call }) }),
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "done") }));

        var big = new string('x', 10000);
        var tool = new FakeTool("echo", big);
        var config = new AgentConfig { ContextLimit = 0, ContextTokenLimit = 0, AutoSummarize = false, ToolResultMaxChars = 100 };
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new[] { tool }, "sys", config);

        var events = new List<AgentEvent>();
        await session.SendAsync("hi", new SyncProgress(events.Add));

        var toolMessage = Assert.Single(session.Messages, m => m.Role == ChatRole.Tool);
        var contextText = Assert.IsType<FunctionResultContent>(Assert.Single(toolMessage.Contents)).Result!.ToString()!;
        Assert.True(contextText.Length < big.Length);
        Assert.Contains("已截断", contextText);

        var ev = Assert.Single(events, e => e.Kind == AgentEventKind.ToolResult);
        Assert.Equal(big, ev.Text);   // 界面仍拿到完整结果
    }

    [Fact]
    public async Task Mid_loop_compacts_old_tool_results_to_placeholder()
    {
        static ChatResponse Call(string id) => new(new List<ChatMessage>
        {
            new(ChatRole.Assistant, new AIContent[] { new FunctionCallContent(id, "echo", new Dictionary<string, object?>()) })
        });

        var client = new FakeChatClient(
            Call("c1"), Call("c2"), Call("c3"),
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "done") }));

        var big = new string('x', 5000);
        var tool = new FakeTool("echo", big);
        var config = new AgentConfig
        {
            ContextLimit = 0, ContextTokenLimit = 100, ContextTrimRatio = 1.0,
            AutoSummarize = false, ToolResultMaxChars = 0
        };
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new[] { tool }, "sys", config);

        await session.SendAsync("hi", null);

        var texts = session.Messages.Where(m => m.Role == ChatRole.Tool)
            .Select(m => ((FunctionResultContent)m.Contents[0]).Result!.ToString()!).ToList();
        Assert.Equal(3, texts.Count);
        Assert.Contains("[较早的工具结果已省略以节省上下文；如需完整内容请重新调用该工具]", texts);
        Assert.Equal(2, texts.Count(t => t == big));   // 最近两个保持完整
    }

    [Fact]
    public async Task Sends_multimodal_user_message_with_image()
    {
        var client = new CapturingChatClient();
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            Array.Empty<IAgentTool>(), "sys", new AgentConfig());

        var contents = new AIContent[]
        {
            new TextContent("看下这张图"),
            new DataContent(new byte[] { 1, 2, 3 }, "image/png")
        };
        await session.SendAsync(contents, null);

        var userMessage = Assert.Single(client.LastMessages!, m => m.Role == ChatRole.User);
        Assert.Equal(2, userMessage.Contents.Count);
        Assert.Contains(userMessage.Contents, c => c is DataContent);
        Assert.Contains(userMessage.Contents, c => c is TextContent { Text: "看下这张图" });
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public IEnumerable<ChatMessage>? LastMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "ok") }));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, new AIContent[] { new TextContent("ok") });
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class SyncProgress : IProgress<AgentEvent>
    {
        private readonly Action<AgentEvent> _sink;
        public SyncProgress(Action<AgentEvent> sink) => _sink = sink;
        public void Report(AgentEvent value) => _sink(value);
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
        public IDictionary<string, object?>? LastArgs { get; private set; }

        public Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
        {
            LastArgs = arguments;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses;

        public FakeChatClient(params ChatResponse[] responses) => _responses = new Queue<ChatResponse>(responses);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "ok") }));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = _responses.Count > 0
                ? _responses.Dequeue()
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
