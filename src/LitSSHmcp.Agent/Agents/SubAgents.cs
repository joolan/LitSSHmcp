using System.Text;
using LitSSHmcp.Core.Models;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 子代理工具 <c>run_subagent</c>：把一次独立的只读取证/调查任务委派到**隔离的上下文**中执行，
/// 子代理用自己的工具循环收集证据，只把**结论摘要**返回主上下文——从而把大日志/多步取证的大量原始输出
/// 挡在主上下文之外（降低 token 与噪声）。
/// </summary>
public static class SubAgents
{
    public const string ToolName = "run_subagent";

    public static IAgentTool Create(
        IChatClient client,
        AgentProviderConfig provider,
        AgentConfig config,
        IReadOnlyList<IAgentTool> subTools)
    {
        var function = new LocalFunction(
            ToolName,
            "把一次独立的只读取证/调查任务委派给子代理在隔离上下文中完成, 只返回结论摘要(大量原始输出不回主上下文)。适合跨多工具的大日志/多步取证; task 需自包含(含 serverId/目标与期望)",
            """{"type":"object","properties":{"task":{"type":"string","description":"交给子代理的独立只读任务, 自包含(含 serverId/目标与期望结论)"},"groups":{"type":"string","description":"可选: 限制子代理可用工具分组(逗号分隔, 如 ssh,log,docker,java)"}},"required":["task"]}""",
            (args, ct) => RunAsync(client, provider, config, subTools, args, ct));

        return new LocalAgentTool(function);
    }

    private static async ValueTask<object?> RunAsync(
        IChatClient client,
        AgentProviderConfig provider,
        AgentConfig config,
        IReadOnlyList<IAgentTool> subTools,
        IDictionary<string, object?> args,
        CancellationToken ct)
    {
        var task = ArgumentReader.ReadString(args, "task");
        if (string.IsNullOrWhiteSpace(task))
            return "(缺少 task)";

        var tools = FilterByGroups(subTools, ArgumentReader.ReadString(args, "groups"));

        const string prompt =
            "你是主代理委派的**子代理**：只做**只读**调查取证，用工具收集事实后**只返回简洁结论**(要点/证据/未决)，不要提问、不要做任何写操作。";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        var session = new AgentSession(client, provider, tools, prompt, config);
        var collector = new TextCollector();
        try
        {
            await session.SendAsync(task!, collector, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return "(子代理超时/取消)" + collector.Tail();
        }
        catch (Exception ex)
        {
            return "(子代理失败) " + ex.Message;
        }

        var text = collector.Result.Trim();
        return string.IsNullOrEmpty(text) ? "(子代理无输出)" : text;
    }

    private static IReadOnlyList<IAgentTool> FilterByGroups(IReadOnlyList<IAgentTool> tools, string? groups)
    {
        if (string.IsNullOrWhiteSpace(groups))
            return tools;
        var allowed = new HashSet<string>(
            groups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        return tools.Where(t => ToolFilter.GroupOf(t.Name) is not { } g || allowed.Contains(g)).ToList();
    }

    /// <summary>收集子代理的最终回答（遇到工具调用则重置，取最后一个 assistant 文本块）。</summary>
    private sealed class TextCollector : IProgress<AgentEvent>
    {
        private readonly StringBuilder _sb = new();

        public void Report(AgentEvent value)
        {
            switch (value.Kind)
            {
                case AgentEventKind.ToolCall:
                    _sb.Clear();
                    break;
                case AgentEventKind.AssistantText when !string.IsNullOrEmpty(value.Text):
                    _sb.Append(value.Text);
                    break;
            }
        }

        public string Result => _sb.ToString();
        public string Tail() => _sb.Length == 0 ? string.Empty : " 部分输出: " + _sb.ToString();
    }
}
