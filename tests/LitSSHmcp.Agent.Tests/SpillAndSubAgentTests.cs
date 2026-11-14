using System.Text.RegularExpressions;
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
    public async Task Spill_read_pages_by_char_budget_and_reports_next_offset()
    {
        var store = new SpillStore(_dir, 7);
        var content = string.Join('\n', Enumerable.Range(0, 50)
            .Select(i => $"line{i}-" + new string('x', 180)));
        var handle = store.Save("big", content);
        var spill = Tool(SpillTools.Create(store, maxResultChars: 1000), "spill");

        var first = (string)(await spill.InvokeAsync(
            Args(("handle", handle), ("limit", 200)), default))!;
        Assert.True(first.Length <= 1000, $"spill read 超出预算: {first.Length}");
        Assert.Contains("下次 offset=", first);

        var match = Regex.Match(first, @"下次 offset=(\d+)");
        Assert.True(match.Success);
        var nextOffset = int.Parse(match.Groups[1].Value);

        var second = (string)(await spill.InvokeAsync(
            Args(("handle", handle), ("offset", nextOffset), ("limit", 200)), default))!;
        Assert.Contains($"{nextOffset}: line{nextOffset}-", second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Spill_read_pages_single_long_line_by_offset()
    {
        var store = new SpillStore(_dir, 7);
        var content = "START-" + new string('x', 2500) + "-END";
        var handle = store.Save("ssh_execute_command", content);
        var spill = Tool(SpillTools.Create(store, maxResultChars: 1000), "spill");

        var first = (string)(await spill.InvokeAsync(Args(("handle", handle), ("offset", 0)), default))!;
        Assert.Contains("单行超长", first);
        Assert.Contains("START-", first);
        Assert.Contains("下次 offset=1", first);
        Assert.True(first.Length <= 1000, $"spill read 超出预算: {first.Length}");

        var second = (string)(await spill.InvokeAsync(Args(("handle", handle), ("offset", 1)), default))!;
        Assert.NotEqual(first, second);

        var page = second;
        var next = 2;
        while (!page.Contains("已到末尾") && next <= 10)
        {
            page = (string)(await spill.InvokeAsync(Args(("handle", handle), ("offset", next)), default))!;
            next++;
        }
        Assert.Contains("-END", page);
        Assert.Contains("已到末尾", page);
    }

    [Fact]
    public async Task Spill_read_long_line_in_multiline_file_supports_column_paging()
    {
        var store = new SpillStore(_dir, 7);
        var longLine = "COL-" + new string('a', 2400) + "-TAIL";
        var handle = store.Save("ssh_execute_command", "header\n" + longLine + "\nfooter");
        var spill = Tool(SpillTools.Create(store, maxResultChars: 1000), "spill");

        var first = (string)(await spill.InvokeAsync(
            Args(("handle", handle), ("offset", 1), ("column", 0)), default))!;
        Assert.Contains("COL-", first);
        Assert.Contains("column=", first);

        var match = Regex.Match(first, @"column=(\d+)");
        Assert.True(match.Success);
        var nextColumn = int.Parse(match.Groups[1].Value);
        var second = (string)(await spill.InvokeAsync(
            Args(("handle", handle), ("offset", 1), ("column", nextColumn)), default))!;
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Spill_json_decodes_long_string_field_newlines()
    {
        var store = new SpillStore(_dir, 7);
        var handle = store.Save("ssh_execute_command",
            "{\"success\":true,\"output\":\"line1\\nline2\\nline3\"}");
        var spill = Tool(SpillTools.Create(store), "spill");

        var text = (string)(await spill.InvokeAsync(
            Args(("action", "json"), ("handle", handle), ("path", "output")), default))!;
        Assert.Contains("line1\nline2\nline3", text);
    }

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
        var spill = Tool(tools, "spill");
        var read = await spill.InvokeAsync(Args(("handle", handle), ("offset", 100), ("limit", 5)), default);
        Assert.Contains("100: line101", (string)read!);
        Assert.Contains("共 500 行", (string)read!);

        var grep = await spill.InvokeAsync(Args(("action", "grep"), ("handle", handle), ("pattern", @"line12\d")), default);
        Assert.Contains("line120", (string)grep!);

        var list = await spill.InvokeAsync(Args(("action", "list")), default);
        Assert.Contains(handle, (string)list!);
    }

    [Fact]
    public async Task Spill_grep_returns_context_windows_for_single_line_json()
    {
        var store = new SpillStore(_dir, 7);
        var json = "{\"users\":[" + string.Join(",", Enumerable.Range(1, 300)
            .Select(i => $"{{\"name\":\"u{i}\",\"riskLevel\":\"高风险\"}}")) + "]}";
        var handle = store.Save("snapshot_accounts", json);
        var spill = Tool(SpillTools.Create(store), "spill");

        var grep = await spill.InvokeAsync(
            Args(("action", "grep"), ("handle", handle), ("pattern", "高风险"), ("max", 5)), default);
        var text = (string)grep!;

        Assert.Contains("高风险", text);
        Assert.Contains("riskLevel", text);
        var stored = File.ReadAllText(Path.Combine(store.Dir, handle));
        Assert.Contains("\n", stored);
        Assert.Contains("  \"users\"", stored);
        Assert.DoesNotContain(json, text);              // 不整行返回
        Assert.True(text.Split('\n').Length <= 5);      // max 生效
    }

    [Fact]
    public async Task Spill_json_reads_json_path_and_pretty_prints_file()
    {
        var store = new SpillStore(_dir, 7);
        var handle = store.Save("snapshot", """
            {"data":{"sections":{"resource":{"status":"ok","dataChars":12},"portmap":{"status":"ok"}}}}
            """);
        var spill = Tool(SpillTools.Create(store), "spill");

        var node = await spill.InvokeAsync(
            Args(("action", "json"), ("handle", handle), ("path", "data.sections.resource")), default);
        var text = (string)node!;
        Assert.Contains("\"status\": \"ok\"", text);
        Assert.Contains("\"dataChars\": 12", text);

        var root = await spill.InvokeAsync(
            Args(("action", "json"), ("handle", handle), ("path", "")), default);
        Assert.Contains("data=object", (string)root!);
    }

    [Fact]
    public void Spill_section_index_lists_snapshot_sections()
    {
        var index = AgentSession.BuildSpillSectionIndex(
            """{"data":{"sections":{"resource":{"status":"ok","dataChars":10},"portmap":{"status":"ok","dataChars":20}}}}""");

        Assert.Contains("resource(ok,10)", index);
        Assert.Contains("portmap(ok,20)", index);
        Assert.Contains("ssh_snapshot_get", index);
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
    public async Task Spill_tool_result_is_not_respilled()
    {
        var store = new SpillStore(_dir, 7);
        var call = new FunctionCallContent("c1", "spill", new Dictionary<string, object?>());
        var client = new FakeChatClient(
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, new AIContent[] { call }) }),
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "done") }));
        var config = new AgentConfig
        {
            ContextLimit = 0, ContextTokenLimit = 0, AutoSummarize = false,
            ToolResultMaxChars = 100, SpillLargeToolResults = true
        };
        var session = new AgentSession(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new[] { new FakeTool("spill", new string('y', 1000)) }, "sys", config, spill: store);

        await session.SendAsync("hi", null);

        var text = Assert.IsType<FunctionResultContent>(
            Assert.Single(Assert.Single(session.Messages, m => m.Role == ChatRole.Tool).Contents)).Result!.ToString()!;
        Assert.Contains("spill 读取结果仍超过", text);
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Sub_agent_spills_large_tool_result_to_shared_store()
    {
        var store = new SpillStore(_dir, 7);
        var call = new FunctionCallContent("c1", "echo", new Dictionary<string, object?>());
        var client = new FakeChatClient(
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, new AIContent[] { call }) }),
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "sub-summary") }));
        var big = new string('x', 1000);
        var config = new AgentConfig
        {
            MaxToolIterations = 3, ContextLimit = 0, ContextTokenLimit = 0, AutoSummarize = false,
            ToolResultMaxChars = 100, SpillLargeToolResults = true
        };
        var tool = SubAgents.Create(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            config, new IAgentTool[] { new FakeTool("echo", big), new FakeTool("spill", "noop") }, spill: store);

        var result = (await tool.InvokeAsync(Args(("task", "read logs on server X")), default))!.ToString()!;

        Assert.Equal("sub-summary", result);
        var handle = Assert.Single(store.List());
        Assert.Equal(big, await File.ReadAllTextAsync(Path.Combine(store.Dir, handle)));
        Assert.Contains("spill(action=\"read\"", client.LastMessages!.First(m => m.Role == ChatRole.System).Text);
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
    public async Task Sub_agent_does_not_return_raw_tool_output_to_parent()
    {
        var call = new FunctionCallContent("c1", "echo", new Dictionary<string, object?>());
        var client = new FakeChatClient(
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, new AIContent[] { call }) }),
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "sub-summary") }));
        var tool = SubAgents.Create(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new AgentConfig { MaxToolIterations = 3 }, new IAgentTool[] { new FakeTool("echo", new string('x', 10000)) });

        var result = (await tool.InvokeAsync(Args(("task", "check server X")), default))!.ToString()!;

        Assert.Equal("sub-summary", result);
        Assert.DoesNotContain(new string('x', 100), result);
    }

    [Fact]
    public async Task Sub_agent_summary_is_capped_before_returning_to_parent()
    {
        var client = new FakeChatClient(new ChatResponse(new List<ChatMessage>
        {
            new(ChatRole.Assistant, new string('x', 2000))
        }));
        var tool = SubAgents.Create(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new AgentConfig { MaxToolIterations = 3, SubAgentSummaryMaxChars = 500 },
            Array.Empty<IAgentTool>());

        var result = (await tool.InvokeAsync(Args(("task", "check server X")), default))!.ToString()!;

        Assert.Contains("子代理摘要已按上限截断", result);
        Assert.True(result.Length < 700);
    }

    [Fact]
    public async Task Sub_agent_prompt_includes_base_and_safety_rules()
    {
        var client = new FakeChatClient(new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "sub-answer") }));
        var tool = SubAgents.Create(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new AgentConfig { MaxToolIterations = 3 }, Array.Empty<IAgentTool>());

        await tool.InvokeAsync(Args(("task", "check server X")), default);

        var system = client.LastMessages!.First(m => m.Role == ChatRole.System).Text!;
        Assert.StartsWith("你是 LitSSH 运维助手", system);   // 复用主代理 BasePrompt
        Assert.Contains("子代理约束", system);
        Assert.Contains("不可信数据", system);
        Assert.Contains("只读", system);
        Assert.Contains("不得调用任何写", system);
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
    public async Task Sub_agent_reports_usage_delta_to_shared_ledger()
    {
        var usage = new UsageDetails { InputTokenCount = 120, OutputTokenCount = 30 };
        var client = new FakeChatClient(usage,
            new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "sub-answer") }));
        long input = 0, output = 0;
        var tool = SubAgents.Create(client, new AgentProviderConfig { Model = "m", ApiKey = "k" },
            new AgentConfig { MaxToolIterations = 3 }, Array.Empty<IAgentTool>(),
            onUsageDelta: (i, o) => { input += i; output += o; });

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

        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue()
                : new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, "ok") }));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
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
