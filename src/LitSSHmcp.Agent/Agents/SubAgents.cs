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
        IReadOnlyList<IAgentTool> subTools,
        string? restrictionNote = null)
    {
        // 同一子任务的调用计数（防止主模型对着“无输出”的子任务反复重试导致卡住）
        var taskCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        var function = new LocalFunction(
            ToolName,
            "把一次独立的只读取证/调查任务委派给子代理在隔离上下文中完成, 只返回结论摘要(大量原始输出不回主上下文)。适合跨多工具的大日志/多步取证; task 需自包含(含 serverId/目标与期望)",
            """{"type":"object","properties":{"task":{"type":"string","description":"交给子代理的独立只读任务, 自包含(含 serverId/目标与期望结论)"},"groups":{"type":"string","description":"可选: 限制子代理可用工具分组(逗号分隔, 如 ssh,log,docker,java)"}},"required":["task"]}""",
            (args, ct) => RunAsync(client, provider, config, subTools, restrictionNote, taskCounts, args, ct));

        return new LocalAgentTool(function, readOnly: true);
    }

    private static async ValueTask<object?> RunAsync(
        IChatClient client,
        AgentProviderConfig provider,
        AgentConfig config,
        IReadOnlyList<IAgentTool> subTools,
        string? restrictionNote,
        IDictionary<string, int> taskCounts,
        IDictionary<string, object?> args,
        CancellationToken ct)
    {
        var task = ArgumentReader.ReadString(args, "task");
        if (string.IsNullOrWhiteSpace(task))
            return "(缺少 task)";

        // 同一任务限次：已被委派过 2 次仍无结论时直接阻止，避免无限重试。
        var key = Normalize(task!);
        taskCounts.TryGetValue(key, out var count);
        if (count >= 2)
            return $"(同一子任务已委派 {count} 次仍无结论，已阻止重复调用；请不要再对同一任务调用 run_subagent——请改换策略、缩小任务范围，或调整会话工具/只读设置后自行继续。)";
        taskCounts[key] = count + 1;

        var tools = FilterByGroups(subTools, ArgumentReader.ReadString(args, "groups"));

        var prompt =
            "你是主代理委派的**子代理**：只做**只读**调查取证，用工具收集事实后**只返回简洁结论**(要点/证据/未决)，不要提问、不要做任何写操作。"
            + (string.IsNullOrEmpty(restrictionNote) ? string.Empty : restrictionNote);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(4));

        // 子代理轮数上限压低（避免长时间空转）：默认不超过 8 轮。
        var maxIterations = config.MaxToolIterations > 0 ? Math.Min(config.MaxToolIterations, 8) : 8;
        var session = new AgentSession(client, provider, tools, prompt, config, maxIterationsOverride: maxIterations);
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
        if (string.IsNullOrEmpty(text))
        {
            var detail = collector.LastError is { Length: > 0 } e ? $"最后错误：{e}"
                : collector.LastTool is { Length: > 0 } t ? $"最后动作：{t}（未产出结论）"
                : "无工具输出";
            return $"(子代理未产出结论 · {detail})。请勿重复委派同一任务；请基于已有信息继续、缩小任务范围，或调整会话工具/只读设置。";
        }
        return text;
    }

    private static string Normalize(string task)
    {
        var s = string.Join(' ', task.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= 240 ? s : s[..240];
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

        public string? LastTool { get; private set; }
        public string? LastError { get; private set; }

        public void Report(AgentEvent value)
        {
            switch (value.Kind)
            {
                case AgentEventKind.ToolCall:
                    _sb.Clear();
                    LastTool = value.ToolName;
                    break;
                case AgentEventKind.AssistantText when !string.IsNullOrEmpty(value.Text):
                    _sb.Append(value.Text);
                    break;
                case AgentEventKind.Error when !string.IsNullOrEmpty(value.Text):
                    LastError = value.Text;
                    break;
            }
        }

        public string Result => _sb.ToString();
        public string Tail() => _sb.Length == 0 ? string.Empty : " 部分输出: " + _sb.ToString();
    }
}
