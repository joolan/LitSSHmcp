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
    public async Task Turn_budget_wall_clock_stops_loop_with_error()
    {
        // 单轮墙钟预算：工具耗时(1.2s)超过预算(1s)后，第 2 轮模型调用前停止本轮并报 Error。
        var call = new FunctionCallContent("c1", "slow", new Dictionary<string, object?>());
        var assistantWithCall = new ChatMessage(ChatRole.Assistant, new AIContent[] { call });
        var client = new FakeChatClient(new ChatResponse(new List<ChatMessage> { assistantWithCall }));
        var tool = new FakeTool("slow", "ok", delayMs: 1200);
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new[] { tool }, "sys", new AgentConfig { Budget = new AgentBudgetConfig { MaxTurnSeconds = 1 } });

        var events = new List<AgentEvent>();
        await session.SendAsync("hi", new SyncProgress(events.Add));

        Assert.NotNull(tool.LastArgs);                                          // 首轮工具照常执行
        Assert.Contains(events, e => e.Kind == AgentEventKind.Error && e.Text!.Contains("预算超限"));
        Assert.DoesNotContain(events, e => e.Kind == AgentEventKind.Done);      // 未正常结束
    }

    [Fact]
    public async Task Denied_tool_is_blocked_without_invoking()
    {
        var call = new FunctionCallContent("c1", "echo", new Dictionary<string, object?> { ["x"] = "y" });
        var assistantWithCall = new ChatMessage(ChatRole.Assistant, new AIContent[] { call });
        var final = new ChatMessage(ChatRole.Assistant, "done");
        var client = new FakeChatClient(
            new ChatResponse(new List<ChatMessage> { assistantWithCall }),
            new ChatResponse(new List<ChatMessage> { final }));

        var tool = new FakeTool("echo", "pong");
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new[] { tool }, "sys", new AgentConfig());
        session.SetToolPolicy(new Dictionary<string, AgentToolPolicy> { ["echo"] = AgentToolPolicy.Deny });

        var events = new List<AgentEvent>();
        await session.SendAsync("hi", new SyncProgress(events.Add));

        Assert.Null(tool.LastArgs);   // 拒绝策略 → 工具未被真正调用
        Assert.Contains(events, e => e.Kind == AgentEventKind.ToolResult && !e.ToolSuccess && e.Text!.Contains("禁用"));
    }

    [Fact]
    public async Task Plan_mode_blocks_write_tool_and_prepends_notice()
    {
        var call = new FunctionCallContent("c1", "ssh_execute_command", new Dictionary<string, object?>());
        var assistantWithCall = new ChatMessage(ChatRole.Assistant, new AIContent[] { call });
        var final = new ChatMessage(ChatRole.Assistant, "planned");
        var client = new FakeChatClient(
            new ChatResponse(new List<ChatMessage> { assistantWithCall }),
            new ChatResponse(new List<ChatMessage> { final }));

        var writeTool = new FakeTool("ssh_execute_command", "ran");   // 默认 readOnly=false
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new[] { writeTool }, "sys", new AgentConfig());
        session.SetPlanMode(true);

        var events = new List<AgentEvent>();
        await session.SendAsync("重启nginx", new SyncProgress(events.Add));

        Assert.Null(writeTool.LastArgs);   // 计划模式拦截写工具
        Assert.Contains(events, e => e.Kind == AgentEventKind.ToolResult && !e.ToolSuccess && e.Text!.Contains("计划模式"));
        var userMsg = session.Messages.First(m => m.Role == ChatRole.User);
        Assert.Contains("【计划模式】", userMsg.Text);   // 每轮提示附在用户消息尾部
    }

    [Fact]
    public async Task Plan_mode_allows_readonly_tool()
    {
        var call = new FunctionCallContent("c1", "ssh_list_servers", new Dictionary<string, object?>());
        var assistantWithCall = new ChatMessage(ChatRole.Assistant, new AIContent[] { call });
        var final = new ChatMessage(ChatRole.Assistant, "done");
        var client = new FakeChatClient(
            new ChatResponse(new List<ChatMessage> { assistantWithCall }),
            new ChatResponse(new List<ChatMessage> { final }));

        var readTool = new FakeTool("ssh_list_servers", "ok", readOnly: true);
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new[] { readTool }, "sys", new AgentConfig());
        session.SetPlanMode(true);

        await session.SendAsync("看看有哪些服务器", new SyncProgress(_ => { }));

        Assert.NotNull(readTool.LastArgs);   // 只读工具在计划模式下仍可调用
    }

    [Fact]
    public async Task Plan_mode_allows_safe_probe_tool()
    {
        var call = new FunctionCallContent("c1", "ssh_snapshot_refresh", new Dictionary<string, object?>());
        var assistantWithCall = new ChatMessage(ChatRole.Assistant, new AIContent[] { call });
        var final = new ChatMessage(ChatRole.Assistant, "refreshed");
        var client = new FakeChatClient(
            new ChatResponse(new List<ChatMessage> { assistantWithCall }),
            new ChatResponse(new List<ChatMessage> { final }));

        // 非只读但只做探测（写本地快照）的工具在计划模式下应放行
        var probe = new FakeTool("ssh_snapshot_refresh", "ok");
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new[] { probe }, "sys", new AgentConfig());
        session.SetPlanMode(true);

        await session.SendAsync("刷新快照", null);

        Assert.NotNull(probe.LastArgs);
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
    public async Task Summary_usage_is_counted_in_session_totals()
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

        var client = new SequencedUsageChatClient(
            ("SUMMARY", new UsageDetails { InputTokenCount = 50, OutputTokenCount = 5 }),
            ("final", new UsageDetails { InputTokenCount = 100, OutputTokenCount = 10 }));
        var config = new AgentConfig { ContextLimit = 3, ContextTokenLimit = 0, AutoSummarize = true };
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            Array.Empty<IAgentTool>(), "SYS", config, history);

        await session.SendAsync("hi", null);

        // 摘要（非主循环）与主请求的 usage 都应计入会话总量/成本
        Assert.Equal(150L, session.SessionInputTokens);
        Assert.Equal(15L, session.SessionOutputTokens);
        Assert.Equal(2, session.SessionRequestCount);
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

    [Fact]
    public void RepairOrphanToolCalls_fills_missing_result_from_history()
    {
        var call = new FunctionCallContent("c1", "echo", new Dictionary<string, object?>());
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "u1"),
            new(ChatRole.Assistant, new AIContent[] { call }),   // 孤儿：没有 tool 结果
            new(ChatRole.User, "u2"),
            new(ChatRole.Assistant, "a2"),
        };

        var session = new AgentSession(new FakeChatClient(),
            new AgentProviderConfig { Model = "m", ApiKey = "k" }, Array.Empty<IAgentTool>(), "SYS",
            new AgentConfig(), history);

        var tool = Assert.Single(session.Messages, m => m.Role == ChatRole.Tool);
        Assert.Equal("c1", Assert.IsType<FunctionResultContent>(Assert.Single(tool.Contents)).CallId);
        // 合成结果紧随 assistant(call) 之后
        Assert.IsType<FunctionCallContent>(Assert.Single(session.Messages[2].Contents));
        Assert.Equal(ChatRole.Tool, session.Messages[3].Role);
    }

    [Fact]
    public async Task RepairOrphanToolCalls_fills_missing_result_before_send()
    {
        var session = new AgentSession(new FakeChatClient(new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "done") })),
            new AgentProviderConfig { Model = "m", ApiKey = "k" }, Array.Empty<IAgentTool>(), "SYS", new AgentConfig());

        // 直接注入孤儿（模拟外部损坏/编辑后残留），发送前应被修复
        var call = new FunctionCallContent("cX", "echo", new Dictionary<string, object?>());
        session.Messages.Add(new ChatMessage(ChatRole.Assistant, new AIContent[] { call }));

        await session.SendAsync("hi", null);

        var tool = Assert.Single(session.Messages,
            m => m.Role == ChatRole.Tool && ((FunctionResultContent)m.Contents[0]).CallId == "cX");
        Assert.NotNull(tool);
    }

    [Fact]
    public async Task Records_cached_input_tokens_from_usage()
    {
        var usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 10, CachedInputTokenCount = 80 };
        var session = new AgentSession(new UsageEmittingChatClient(usage),
            new AgentProviderConfig { Model = "m", ApiKey = "k" }, Array.Empty<IAgentTool>(), "sys", new AgentConfig());

        await session.SendAsync("hi", null);

        Assert.Equal(80L, session.LastRequestCachedInputTokens!.Value);
        Assert.Equal(80L, session.SessionCachedInputTokens);
        Assert.Equal(100L, session.SessionInputTokens);
    }

    [Fact]
    public void AddChildUsage_accumulates_into_parent_session_totals()
    {
        var session = new AgentSession(new CapturingChatClient(),
            new AgentProviderConfig { Model = "m", ApiKey = "k" }, Array.Empty<IAgentTool>(), "sys", new AgentConfig());

        session.AddChildUsage(120, 30);
        session.AddChildUsage(5, 0);

        Assert.Equal(125L, session.SessionInputTokens);
        Assert.Equal(30L, session.SessionOutputTokens);
    }

    [Fact]
    public async Task Usage_delta_callback_receives_each_response_usage()
    {
        var usage = new UsageDetails { InputTokenCount = 120, OutputTokenCount = 30 };
        long input = 0, output = 0;
        var session = new AgentSession(new UsageEmittingChatClient(usage),
            new AgentProviderConfig { Model = "m", ApiKey = "k" }, Array.Empty<IAgentTool>(), "sys", new AgentConfig(),
            onUsageDelta: (i, o) => { input += i; output += o; });

        await session.SendAsync("hi", null);

        Assert.Equal(120L, input);
        Assert.Equal(30L, output);
    }

    [Fact]
    public async Task Budget_check_stops_session_after_usage_exceeds_limit()
    {
        var usage = new UsageDetails { InputTokenCount = 1_000_000, OutputTokenCount = 0 };
        var checks = 0;
        var session = new AgentSession(new UsageEmittingChatClient(usage),
            new AgentProviderConfig { Model = "m", ApiKey = "k" }, Array.Empty<IAgentTool>(), "sys", new AgentConfig(),
            budgetCheck: (_, _) => { checks++; return false; });

        await session.SendAsync("hi", null);

        Assert.Equal(1, checks);
        Assert.Equal(1_000_000L, session.SessionInputTokens);
    }

    [Fact]
    public async Task Executes_readonly_calls_in_parallel_keeping_call_order()
    {
        var callA = new FunctionCallContent("c1", "read_a", new Dictionary<string, object?>());
        var callB = new FunctionCallContent("c2", "read_b", new Dictionary<string, object?>());
        var client = new FakeChatClient(
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, new AIContent[] { callA, callB }) }),
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "done") }));

        // 两个工具都必须等到"对方也已开始"才返回：并行执行时立即满足；串行执行时第一个会超时 → 返回 NOT-PARALLEL。
        using var bothStarted = new CountdownEvent(2);
        async Task<object?> Probe(string result, CancellationToken ct)
        {
            bothStarted.Signal();
            if (!bothStarted.Wait(TimeSpan.FromSeconds(3)))
                return "NOT-PARALLEL";
            await Task.Delay(20, ct);
            return result;
        }

        var toolA = new FakeTool("read_a", "A", readOnly: true, invoke: ct => Probe("A", ct));
        var toolB = new FakeTool("read_b", "B", readOnly: true, invoke: ct => Probe("B", ct));
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new IAgentTool[] { toolA, toolB }, "sys", new AgentConfig { ContextLimit = 0, ContextTokenLimit = 0 });

        var events = new List<AgentEvent>();
        await session.SendAsync("hi", new SyncProgress(events.Add));

        Assert.DoesNotContain(events, e => e.Text == "NOT-PARALLEL");

        // ToolCall 与 ToolResult 均按原始调用顺序推送（结果精确回填）
        var kinds = events.Where(e => e.Kind is AgentEventKind.ToolCall or AgentEventKind.ToolResult)
            .Select(e => $"{e.Kind}:{e.ToolName}").ToList();
        Assert.Equal(new[] { "ToolCall:read_a", "ToolCall:read_b", "ToolResult:read_a", "ToolResult:read_b" }, kinds);

        var toolMessages = session.Messages.Where(m => m.Role == ChatRole.Tool).ToList();
        Assert.Equal(2, toolMessages.Count);
        Assert.Equal("c1", Assert.IsType<FunctionResultContent>(toolMessages[0].Contents[0]).CallId);
        Assert.Equal("c2", Assert.IsType<FunctionResultContent>(toolMessages[1].Contents[0]).CallId);
        Assert.Contains("A", toolMessages.Select(m => ((FunctionResultContent)m.Contents[0]).Result?.ToString()));
        Assert.Contains("B", toolMessages.Select(m => ((FunctionResultContent)m.Contents[0]).Result?.ToString()));
    }

    [Fact]
    public async Task Consecutive_tool_failures_trip_the_breaker()
    {
        static ChatResponse Call(string id) => new(new List<ChatMessage>
        {
            new(ChatRole.Assistant, new AIContent[] { new FunctionCallContent(id, "nope", new Dictionary<string, object?>()) })
        });

        var client = new FakeChatClient(
            Call("c1"), Call("c2"), Call("c3"), Call("c4"), Call("c5"),
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "done") }));
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            Array.Empty<IAgentTool>(), "sys", new AgentConfig { MaxToolIterations = 20 });

        var events = new List<AgentEvent>();
        await session.SendAsync("hi", new SyncProgress(events.Add));

        Assert.Contains(events, e => e.Kind == AgentEventKind.Error);
        Assert.DoesNotContain(events, e => e.Kind == AgentEventKind.Done);
        Assert.Equal(4, session.Messages.Count(m => m.Role == ChatRole.Tool));   // 连续 4 次失败即熔断
    }

    [Fact]
    public async Task Log_tool_result_keeps_tail_of_oversized_output()
    {
        var call = new FunctionCallContent("c1", "docker_query", new Dictionary<string, object?>());
        var client = new FakeChatClient(
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, new AIContent[] { call }) }),
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "done") }));

        var content = "START-MARKER" + new string('x', 400) + "LATEST-LINE";
        var tool = new FakeTool("docker_query", content);
        var config = new AgentConfig { ContextLimit = 0, ContextTokenLimit = 0, ToolResultMaxChars = 100 };
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new[] { tool }, "sys", config);

        await session.SendAsync("hi", null);

        var toolMessage = Assert.Single(session.Messages, m => m.Role == ChatRole.Tool);
        var text = Assert.IsType<FunctionResultContent>(Assert.Single(toolMessage.Contents)).Result!.ToString()!;
        Assert.Contains("LATEST-LINE", text);          // 保留最新日志（尾部）
        Assert.DoesNotContain("START-MARKER", text);   // 头部被截断
    }

    [Fact]
    public void Context_breakdown_splits_roles_summary_and_tools()
    {
        var tool = new FakeTool("echo", "r");
        var session = new AgentSession(new FakeChatClient(), new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new IAgentTool[] { tool }, "SYS", new AgentConfig());
        session.Messages.Add(new ChatMessage(ChatRole.User, "你好"));
        session.Messages.Add(new ChatMessage(ChatRole.Assistant, "abcdefgh"));
        session.Messages.Add(new ChatMessage(ChatRole.Tool, new AIContent[] { new FunctionResultContent("c1", "result") }));
        session.RestoreSummary("较早对话摘要");

        var breakdown = session.GetContextBreakdown();

        Assert.True(breakdown.SystemPrompt > 0);
        Assert.True(breakdown.ToolDefinitions > 0);
        Assert.True(breakdown.User > 0);
        Assert.True(breakdown.Assistant > 0);
        Assert.True(breakdown.ToolResults > 0);
        Assert.True(breakdown.Summary > 0);
        Assert.Equal(breakdown.MessagesTotal + breakdown.ToolDefinitions, breakdown.RequestTotal);
        // 摘要单列，不混入系统提示
        Assert.Equal(TokenEstimator.Estimate(new ChatMessage(ChatRole.System, "SYS")), breakdown.SystemPrompt);
    }

    [Fact]
    public async Task SendAsync_notifies_round_boundary_once_per_user_message()
    {
        var final = new ChatMessage(ChatRole.Assistant, "done");
        var client = new FakeChatClient(new ChatResponse(new List<ChatMessage> { final }));

        var notifier = new FakeTool("mcp_approval_round", "ok");
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            Array.Empty<IAgentTool>(), "sys", new AgentConfig(), roundNotifier: notifier);

        await session.SendAsync("第一条", new SyncProgress(_ => { }));
        var firstRound = notifier.LastArgs?["roundId"]?.ToString();
        Assert.False(string.IsNullOrEmpty(firstRound));

        await session.SendAsync("第二条", new SyncProgress(_ => { }));
        var secondRound = notifier.LastArgs?["roundId"]?.ToString();
        Assert.False(string.IsNullOrEmpty(secondRound));
        Assert.NotEqual(firstRound, secondRound);   // 每条用户消息 = 新轮次
    }

    [Fact]
    public async Task SendAsync_survives_round_notifier_failure()
    {
        var client = new FakeChatClient(new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "done") }));
        var notifier = new FakeTool("mcp_approval_round", "ok",
            invoke: _ => throw new InvalidOperationException("boom"));
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            Array.Empty<IAgentTool>(), "sys", new AgentConfig(), roundNotifier: notifier);

        var events = new List<AgentEvent>();
        await session.SendAsync("hi", new SyncProgress(events.Add));

        // 轮次信号失败不影响对话（降级 = 多弹审批，不误放行）
        Assert.Contains(events, e => e.Kind == AgentEventKind.Done);
    }

    [Fact]
    public async Task Retrieval_context_is_injected_for_request_and_removed_after_turn()
    {
        var client = new CapturingChatClient();
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            Array.Empty<IAgentTool>(), "sys", new AgentConfig(),
            recall: (_, _, _) => Task.FromResult<string?>("记忆内容: 先 df 定位分区"));
        var events = new List<AgentEvent>();

        await session.SendAsync("磁盘满", new SyncProgress(events.Add));

        Assert.NotNull(client.LastMessages);
        Assert.Contains(client.LastMessages!,
            m => m.Role == ChatRole.System && (m.Text ?? string.Empty).Contains("记忆内容"));
        Assert.DoesNotContain(session.Messages,
            m => m.Role == ChatRole.System && (m.Text ?? string.Empty).Contains("记忆内容"));
        Assert.Contains(events, e => e.Kind == AgentEventKind.MemoryRecall);
    }

    [Fact]
    public async Task Retrieval_context_does_not_accumulate_across_turns()
    {
        var client = new CapturingChatClient();
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            Array.Empty<IAgentTool>(), "sys", new AgentConfig(),
            recall: (_, _, _) => Task.FromResult<string?>("记忆内容: 先 df 定位分区"));

        await session.SendAsync("第一轮", new SyncProgress(_ => { }));
        await session.SendAsync("第二轮", new SyncProgress(_ => { }));

        Assert.Equal(0, session.Messages.Count(m =>
            m.Role == ChatRole.System && (m.Text ?? string.Empty).Contains("记忆内容")));
        Assert.Equal(2, session.Messages.Count(m => m.Role == ChatRole.User));
    }

    [Fact]
    public async Task Retrieval_failures_emit_warning_and_turn_still_completes()
    {
        var client = new FakeChatClient(new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "done") }));
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            Array.Empty<IAgentTool>(), "sys", new AgentConfig(),
            recall: (_, _, _) => Task.FromException<string?>(new InvalidOperationException("embedding down")),
            graphContext: (_, _) => Task.FromException<AssetGraphContextResult?>(
                new InvalidOperationException("graph down")));
        var events = new List<AgentEvent>();

        // 显式资产 ID 让图谱门控放行，从而覆盖“图谱失败只告警、不阻断本轮”
        await session.SendAsync("check ssh:s1", new SyncProgress(events.Add));

        Assert.Contains(events, e => e.Kind == AgentEventKind.Warning && e.Text!.Contains("记忆召回失败"));
        Assert.Contains(events, e => e.Kind == AgentEventKind.Warning && e.Text!.Contains("资产图谱检索失败"));
        Assert.Contains(events, e => e.Kind == AgentEventKind.Done);
    }

    private sealed class SequencedUsageChatClient : IChatClient
    {
        private readonly Queue<(string Text, UsageDetails Usage)> _responses;

        public SequencedUsageChatClient(params (string Text, UsageDetails Usage)[] responses) =>
            _responses = new Queue<(string, UsageDetails)>(responses);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var (text, usage) = _responses.Count > 0
                ? _responses.Dequeue()
                : ("ok", new UsageDetails());
            return Task.FromResult(new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, text) })
            {
                Usage = usage
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var (text, usage) = _responses.Count > 0
                ? _responses.Dequeue()
                : ("ok", new UsageDetails());
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                new AIContent[] { new TextContent(text), new UsageContent(usage) });
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class UsageEmittingChatClient : IChatClient
    {
        private readonly UsageDetails _usage;
        public UsageEmittingChatClient(UsageDetails usage) => _usage = usage;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "ok") }));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, new AIContent[] { new TextContent("ok") });
            yield return new ChatResponseUpdate(ChatRole.Assistant, new AIContent[] { new UsageContent(_usage) });
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
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
        private readonly bool _readOnly;
        private readonly int _delayMs;
        private readonly Func<CancellationToken, Task<object?>>? _invoke;

        public FakeTool(string name, object? result, bool readOnly = false, int delayMs = 0,
            Func<CancellationToken, Task<object?>>? invoke = null)
        {
            Name = name;
            _result = result;
            _readOnly = readOnly;
            _delayMs = delayMs;
            _invoke = invoke;
            Tool = AIFunctionFactory.Create(() => _result, name);
        }

        public string Name { get; }
        public AITool Tool { get; }
        public bool Destructive => false;
        public bool ReadOnly => _readOnly;
        public string? ToolGroup => null;
        public IDictionary<string, object?>? LastArgs { get; private set; }

        public async Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
        {
            LastArgs = arguments;
            if (_invoke is not null)
                return await _invoke(ct);
            if (_delayMs > 0)
                await Task.Delay(_delayMs, ct);
            return _result;
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

    /// <summary>阻塞在模型调用上的客户端：用于确定性地制造“turn 进行中”窗口。</summary>
    private sealed class BlockingChatClient : IChatClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "ok") });
        }

        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // 主循环走流式通道：在这里阻塞，制造“turn 进行中”窗口
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, new AIContent[] { new TextContent("ok") });
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task SendAsync_rejects_concurrent_turn_and_recovers_after_finish()
    {
        // §4.2 重入保护：同一会话 turn 进行中再次 SendAsync 直接拒绝（历史是裸 List，不能并发写）
        var client = new BlockingChatClient();
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            Array.Empty<IAgentTool>(), "sys", new AgentConfig());

        var first = session.SendAsync("a", null);
        // 诊断：第一轮必须到达模型调用（否则失败并带出真实异常），避免无声挂死
        var winner = await Task.WhenAny(client.Entered, Task.Delay(TimeSpan.FromSeconds(10)));
        if (winner != client.Entered)
            Assert.Fail(first.IsFaulted
                ? "第一轮在到达模型调用前抛出: " + first.Exception!.GetBaseException()
                : "第一轮 10 秒内未到达模型调用（疑似卡在前置步骤）");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendAsync("b", null));
        Assert.Contains("并发", ex.Message);

        client.Release();
        await first.WaitAsync(TimeSpan.FromSeconds(10));   // 第一轮正常收尾，重入闸复位

        // 收尾后可再次发送（闸复位）
        var second = session.SendAsync("c", null);
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, session.Messages.Count(m => m.Role == ChatRole.User));   // 被拒的 "b" 未入历史
    }

    [Fact]
    public async Task Worklog_records_successful_mutating_tool_turn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-worklog-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = WorkLogStore.TryCreate(
                new AgentWorkLogConfig { Enabled = true, Dir = dir }, dir, null, scopeByOwner: false)!;
            var call = new FunctionCallContent("c1", "ssh_execute_command",
                new Dictionary<string, object?> { ["serverId"] = "web01", ["command"] = "uptime" });
            var client = new FakeChatClient(
                new ChatResponse(new List<ChatMessage>
                {
                    new(ChatRole.Assistant, new AIContent[] { call })
                }),
                new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "完成") }));

            var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
                new[] { new FakeTool("ssh_execute_command", "ok") }, "sys", new AgentConfig(),
                workLog: store);

            await session.SendAsync("执行 uptime", null);

            var path = store.GetDailyPath(DateTime.Now);
            Assert.True(File.Exists(path));
            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("ssh_execute_command", text);
            Assert.Contains("ssh:web01", text);
            Assert.Contains("uptime", text);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }
}
