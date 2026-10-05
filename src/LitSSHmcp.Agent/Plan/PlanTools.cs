using System.Text.RegularExpressions;

namespace LitSSHmcp.Agent;

/// <summary>一条任务计划项。</summary>
public sealed record PlanItem(bool Done, string Text);

/// <summary>
/// 提供给模型的**任务计划工具**（本地、非 MCP）：多步任务先规划、执行中持续更新完成情况；
/// UI 据此显示进度。作为"单代理内规划器"的落地手段。
/// </summary>
public static class PlanTools
{
    private static readonly Regex ItemPattern = new(@"^[-*]\s+\[([ xX])\]\s+(.*)$", RegexOptions.Compiled);

    public static IAgentTool Create(Action<IReadOnlyList<PlanItem>>? onUpdate)
    {
        var function = new LocalFunction(
            "update_plan",
            "更新任务计划/待办清单：处理多步任务时先提交完整计划，之后每完成一步就再次调用并更新状态。",
            """{"type":"object","properties":{"steps":{"type":"string","description":"完整计划, 每行一个 '- [ ] 步骤' 或 '- [x] 已完成步骤'"}},"required":["steps"]}""",
            args =>
            {
                var items = ParsePlan(ArgumentReader.ReadString(args, "steps") ?? string.Empty);
                onUpdate?.Invoke(items);
                var done = items.Count(i => i.Done);
                return $"计划已更新：{done}/{items.Count} 完成";
            });

        return new LocalAgentTool(function);
    }

    /// <summary>解析计划文本（每行 '- [ ] …' / '- [x] …'，普通行视为未完成项）。</summary>
    public static List<PlanItem> ParsePlan(string text)
    {
        var items = new List<PlanItem>();
        foreach (var rawLine in (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            var match = ItemPattern.Match(line);
            if (match.Success)
                items.Add(new PlanItem(match.Groups[1].Value.Trim().Length > 0, match.Groups[2].Value.Trim()));
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                items.Add(new PlanItem(false, line[2..].Trim()));
            else
                items.Add(new PlanItem(false, line));
        }
        return items;
    }
}
