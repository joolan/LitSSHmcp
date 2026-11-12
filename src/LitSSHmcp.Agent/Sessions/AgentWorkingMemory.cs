using System.Text;
using System.Text.Json;

namespace LitSSHmcp.Agent;

/// <summary>
/// 单会话的小型任务工作记忆：当前目标、计划、已确认事实、未完成项。
/// 作为临时 System 上下文注入，不替代完整历史，也不写入 append-only 会话记录。
/// </summary>
public sealed class AgentWorkingMemory
{
    private const int MaxFacts = 8;
    private const int MaxOpenItems = 6;
    private const int MaxPlanItems = 12;
    private const int MaxFactChars = 260;

    private readonly List<string> _facts = new();
    private readonly List<string> _openItems = new();
    private IReadOnlyList<PlanItem> _plan = Array.Empty<PlanItem>();

    public string Goal { get; private set; } = string.Empty;
    public IReadOnlyList<string> Facts => _facts;
    public IReadOnlyList<string> OpenItems => _openItems;
    public IReadOnlyList<PlanItem> Plan => _plan;

    public void SetGoal(string? goal)
    {
        var text = (goal ?? string.Empty).Trim();
        if (text.Length == 0)
            return;

        // 新目标（非“继续/然后/刚才”等追问）时清理上一任务的临时事实，避免串味。
        var continuation = text.Contains("继续", StringComparison.Ordinal) ||
                           text.Contains("接着", StringComparison.Ordinal) ||
                           text.Contains("然后", StringComparison.Ordinal) ||
                           text.Contains("下一步", StringComparison.Ordinal) ||
                           text.Contains("刚才", StringComparison.Ordinal) ||
                           text.Contains("这个", StringComparison.Ordinal) ||
                           text.Contains("上面的", StringComparison.Ordinal);
        if (Goal.Length > 0 && !continuation && !string.Equals(Goal, text, StringComparison.Ordinal))
        {
            _facts.Clear();
            _openItems.Clear();
            _plan = Array.Empty<PlanItem>();
        }
        Goal = text;
    }

    public void SetPlan(IReadOnlyList<PlanItem>? plan) => _plan = plan ?? Array.Empty<PlanItem>();

    /// <summary>导出为小型 JSON，供 agent_sessions.WorkingMemory 持久化。</summary>
    public string ExportJson() => JsonSerializer.Serialize(new WorkingMemoryState
    {
        Goal = Goal,
        Facts = _facts.ToList(),
        OpenItems = _openItems.ToList(),
        Plan = _plan.Select(p => new WorkingMemoryPlanItem { Done = p.Done, Text = p.Text }).ToList()
    });

    /// <summary>从持久化 JSON 恢复；损坏时保持空工作记忆，不阻断会话。</summary>
    public void ImportJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;
        try
        {
            var state = JsonSerializer.Deserialize<WorkingMemoryState>(json);
            if (state is null)
                return;
            Goal = state.Goal ?? string.Empty;
            _facts.Clear();
            _facts.AddRange((state.Facts ?? new List<string>()).Take(MaxFacts));
            _openItems.Clear();
            _openItems.AddRange((state.OpenItems ?? new List<string>()).Take(MaxOpenItems));
            _plan = (state.Plan ?? new List<WorkingMemoryPlanItem>())
                .Where(p => !string.IsNullOrWhiteSpace(p.Text))
                .Select(p => new PlanItem(p.Done, p.Text))
                .Take(MaxPlanItems)
                .ToList();
        }
        catch (JsonException)
        {
            // 损坏的工作记忆不应阻断主流程
        }
    }

    private sealed class WorkingMemoryState
    {
        public string? Goal { get; set; }
        public List<string>? Facts { get; set; }
        public List<string>? OpenItems { get; set; }
        public List<WorkingMemoryPlanItem>? Plan { get; set; }
    }

    private sealed class WorkingMemoryPlanItem
    {
        public bool Done { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    public void RecordToolResult(string toolName, bool ok, string? resultText)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return;

        if (toolName is "spill" or "update_plan")
            return;

        if (!ok)
        {
            AddOpenItem($"{toolName} 失败: {Compact(resultText)}");
            return;
        }

        // 只记录能形成“事实”的只读/取证类结果；写操作与维护工具不进入工作记忆。
        if (!IsFactTool(toolName))
            return;

        var fact = ExtractFact(toolName, resultText);
        if (!string.IsNullOrWhiteSpace(fact))
            AddFact(fact);
    }

    public string? BuildContext(int maxChars = 1800)
    {
        var sb = new StringBuilder();
        if (Goal.Length > 0)
            sb.AppendLine("- 目标: " + Compact(Goal, 300));

        var plan = _plan.Take(MaxPlanItems).ToList();
        if (plan.Count > 0)
        {
            sb.AppendLine("- 计划:");
            foreach (var item in plan)
                sb.AppendLine($"  {(item.Done ? "[x]" : "[ ]")} {Compact(item.Text, 160)}");
        }

        if (_facts.Count > 0)
        {
            sb.AppendLine("- 已确认事实:");
            foreach (var fact in _facts.TakeLast(MaxFacts))
                sb.AppendLine("  - " + Compact(fact, MaxFactChars));
        }

        if (_openItems.Count > 0)
        {
            sb.AppendLine("- 未完成/阻塞:");
            foreach (var item in _openItems.TakeLast(MaxOpenItems))
                sb.AppendLine("  - " + Compact(item, MaxFactChars));
        }

        if (sb.Length == 0)
            return null;

        var text = "## 任务工作记忆(临时, 以实际工具结果为准)\n" + sb.ToString().TrimEnd();
        return text.Length <= maxChars ? text : text[..maxChars] + "\n…[工作记忆已按上限截断]";
    }

    private void AddFact(string fact)
    {
        if (_facts.Contains(fact, StringComparer.Ordinal))
            return;
        _facts.Add(fact);
        if (_facts.Count > MaxFacts)
            _facts.RemoveRange(0, _facts.Count - MaxFacts);
    }

    private void AddOpenItem(string item)
    {
        if (_openItems.Contains(item, StringComparer.Ordinal))
            return;
        _openItems.Add(item);
        if (_openItems.Count > MaxOpenItems)
            _openItems.RemoveRange(0, _openItems.Count - MaxOpenItems);
    }

    private static bool IsFactTool(string name) =>
        name.StartsWith("ssh_snapshot_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("log_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("docker_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("service_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("java_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("mysql_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("postgres_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("redis_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("topology_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("app_health_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("fleet_", StringComparison.OrdinalIgnoreCase) ||
        name == "run_subagent" ||
        name == "ssh_execute_command" ||
        name == "ssh_execute_sudo";

    private static string ExtractFact(string toolName, string? resultText)
    {
        var text = (resultText ?? string.Empty).Trim();
        if (text.Length == 0)
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var parts = new List<string>();
            foreach (var key in new[] { "status", "state", "success", "error", "reason", "hint", "summary" })
            {
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) &&
                    value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                    parts.Add(key + "=" + Compact(value.ToString(), 120));
            }

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("sections", out var sections) &&
                sections.ValueKind == JsonValueKind.Object)
            {
                var names = sections.EnumerateObject().Select(p => p.Name).Take(8);
                parts.Add("sections=" + string.Join(",", names));
            }

            if (parts.Count > 0)
                return toolName + ": " + string.Join("; ", parts);
        }
        catch (JsonException)
        {
            // 非 JSON 结果按文本摘要
        }

        return toolName + ": " + Compact(text, MaxFactChars);
    }

    private static string Compact(string? text, int max = MaxFactChars)
    {
        var value = string.Join(' ', (text ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (value.Length == 0)
            return string.Empty;
        return value.Length <= max ? value : value[..max] + "…";
    }
}
