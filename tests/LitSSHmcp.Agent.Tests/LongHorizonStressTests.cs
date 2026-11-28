using LitSSHmcp.Agent.Sessions;
using LitSSHmcp.Core.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

/// <summary>
/// 长任务/长日志专项评估 §验证建议 的规模化回归：
/// job 等待期间断连一次可恢复、连续 3 次轮末压缩后摘要仍可读、5MB 单行 JSON 代理对安全。
/// 用生成数据/短超时等比压缩时间尺度，可重复运行不依赖真实服务器。
/// </summary>
public class LongHorizonStressTests
{
    [Fact]
    public async Task Job_wait_recovers_after_one_transient_disconnect()
    {
        // 900s 等待的等比压缩：第 1 次状态查询断连，随后恢复 running → succeeded。
        // 旧实现一次异常即丢 jobId；当前容忍 2 次瞬时失败。
        var call = new FunctionCallContent("c1", "job_start",
            new Dictionary<string, object?> { ["operation"] = "topology_discover" });
        var client = new FakeChatClient(
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, new AIContent[] { call }) }),
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "done") }));

        var statusCalls = 0;
        var tools = new IAgentTool[]
        {
            new FakeTool("job_start", """{"success":true,"jobId":"job_stress","state":"running"}"""),
            new FakeTool("job_status", "{}", readOnly: true, invoke: _ =>
            {
                statusCalls++;
                if (statusCalls == 1)
                    return Task.FromException<object?>(new IOException("MCP 连接瞬时中断"));
                return Task.FromResult<object?>(statusCalls == 2
                    ? """{"state":"running","progressDone":1,"progressTotal":2}"""
                    : """{"state":"succeeded"}""");
            }),
            new FakeTool("job_result", """{"result":"RECOVERED-OK"}""", readOnly: true)
        };
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            tools, "sys", new AgentConfig
            {
                MaxToolIterations = 5,
                ContextLimit = 50,
                JobWaitTimeoutSeconds = 30,
                JobPollMinSeconds = 1,
                JobPollMaxSeconds = 1
            });

        var events = new List<AgentEvent>();
        await session.SendAsync("hi", new SyncProgress(events.Add));

        var toolMessage = Assert.Single(session.Messages, m => m.Role == ChatRole.Tool);
        var text = Assert.IsType<FunctionResultContent>(Assert.Single(toolMessage.Contents)).Result?.ToString() ?? "";
        Assert.Equal(3, statusCalls);
        Assert.Contains("RECOVERED-OK", text);     // 断连后仍取到完整结果
        Assert.DoesNotContain("轮询中断", text);
        Assert.Contains(events, e => e.Kind == AgentEventKind.Warning && e.Text!.Contains("1/3"));
    }

    [Fact]
    public void Three_consecutive_compaction_inputs_keep_goal_and_latest_verdict()
    {
        // 连续 3 轮大工具结果 + 结论：每轮压缩输入都必须保留目标骨架与最新错误结论。
        var messages = new List<ChatMessage> { new(ChatRole.User, "TARGET: 排查支付服务 502") };
        for (var round = 1; round <= 3; round++)
        {
            messages.Add(new ChatMessage(ChatRole.Tool, $"第{round}轮日志 " + new string('x', 3000)));
            messages.Add(new ChatMessage(ChatRole.Assistant,
                $"VERDICT-{round}: 上游连接池耗尽，需扩容 max_connections"));

            var transcript = AgentSession.BuildTranscript(messages);

            Assert.Contains("TARGET: 排查支付服务 502", transcript);
            Assert.Contains($"VERDICT-{round}", transcript);
            Assert.True(transcript.Length <= 8400, $"第 {round} 轮摘要输入未受预算约束: {transcript.Length}");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Five_megabyte_single_line_json_is_safe_to_truncate(bool tail)
    {
        var pad = new string('x', 2_500_000);
        var json = "{\"data\":\"" + pad + "😀" + pad + "\",\"tail\":\"LAST\"}";
        Assert.True(json.Length > 5_000_000);

        var result = tail
            ? TextTruncate.Tail(json, 20_000)
            : TextTruncate.Head(json, 20_000);

        Assert.Equal(20_000, result.Length);
        Assert.True(WellFormed(result), "5MB 单行截断产生非法 UTF-16");
        if (tail)
            Assert.Contains("LAST", result);
        else
            Assert.Contains("{\"data\"", result);
    }

    private static bool WellFormed(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                    return false;
                i++;
            }
            else if (char.IsLowSurrogate(text[i]))
                return false;
        }
        return true;
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
        private readonly bool _readOnly;
        private readonly Func<CancellationToken, Task<object?>>? _invoke;

        public FakeTool(string name, object? result, bool readOnly = false,
            Func<CancellationToken, Task<object?>>? invoke = null)
        {
            Name = name;
            _result = result;
            _readOnly = readOnly;
            _invoke = invoke;
            Tool = AIFunctionFactory.Create(() => _result, name);
        }

        public string Name { get; }
        public AITool Tool { get; }
        public bool Destructive => false;
        public bool ReadOnly => _readOnly;
        public string? ToolGroup => null;
        public bool IsLocal => false;
        public IDictionary<string, object?>? LastArgs { get; private set; }

        public async Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
        {
            LastArgs = arguments;
            return _invoke is not null ? await _invoke(ct) : _result;
        }
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses;
        public FakeChatClient(params ChatResponse[] responses) => _responses = new Queue<ChatResponse>(responses);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ChatResponse(new List<ChatMessage>
            {
                new(ChatRole.Assistant, "ok")
            }));

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
                yield return new ChatResponseUpdate(message.Role, message.Contents)
                {
                    FinishReason = response.FinishReason
                };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
