using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent.Scheduling;

/// <summary>AI 创建/修改任务前的确认回调（桌面弹窗；Web 可替换为审批卡片）。返回 false 表示取消。</summary>
public delegate Task<bool> AiTaskToolConfirm(AiTaskConfig draft, string summary, CancellationToken ct);

/// <summary>
/// 提供给模型的本地工具：创建/查询/启停/删除/立即运行 AI 定时任务。
/// 工具只负责配置，不直接执行；到点由桌面 App 的 AiTaskScheduler 领取并运行。
/// </summary>
public static class AiTaskTools
{
    public const string CreateTool = "ai_task_create";
    public const string ListTool = "ai_task_list";
    public const string SetEnabledTool = "ai_task_set_enabled";
    public const string DeleteTool = "ai_task_delete";
    public const string RunNowTool = "ai_task_run_now";
    public const string RunsTool = "ai_task_runs";

    public static IReadOnlyList<IAgentTool> Create(AiTaskStore? store = null, AiTaskToolConfirm? confirm = null)
    {
        store ??= AiTaskStore.Default;

        var create = new LocalFunction(
            CreateTool,
            "创建 AI 定时任务。用户在对话中要求“每天/每周/每月/每隔 N 分钟/某时刻执行某任务”时使用。" +
            "任务到点会创建一个新会话并发送 instruction 给 AI 自主执行。" +
            "默认只读；创建前必须经用户确认。",
            """
            {"type":"object","properties":{
              "name":{"type":"string","description":"任务名称，简短明确，如“每日磁盘巡检”"},
              "instruction":{"type":"string","description":"到点执行的完整自然语言指令，如“巡检所有服务器磁盘状态并给出结论”"},
              "scheduleType":{"type":"string","enum":["once","interval","daily","weekly","monthly"],"description":"调度类型"},
              "timeOfDay":{"type":"string","description":"Daily/Weekly/Monthly 的执行时刻 HH:mm，默认 08:00"},
              "intervalMinutes":{"type":"integer","description":"Interval 的间隔分钟数，至少 5"},
              "weekDays":{"type":"string","description":"Weekly 的周几，逗号分隔，0=周日..6=周六，如 1,3,5"},
              "monthDays":{"type":"string","description":"Monthly 的日期，逗号分隔，1-28，如 1,15"},
              "onceAt":{"type":"string","description":"Once 的执行时间，ISO 格式，如 2026-11-09T08:00:00"},
              "providerId":{"type":"string","description":"可选：指定模型 Id；空=全局默认"},
              "agentId":{"type":"string","description":"可选：指定智能体 Id（chat/ops/db）"},
              "readOnly":{"type":"boolean","description":"是否只读，默认 true；只读任务只暴露只读工具"},
              "timeoutSeconds":{"type":"integer","description":"单次运行超时秒数，默认 1800"},
              "tokenBudget":{"type":"integer","description":"单次运行 token 预算，0=不限"},
              "notifyOnlyOnIssue":{"type":"boolean","description":"是否仅在发现问题时通知，默认 true"}
            },"required":["name","instruction","scheduleType"]}
            """,
            async (args, ct) =>
            {
                var task = new AiTaskConfig
                {
                    Name = Trim(ArgumentReader.ReadString(args, "name")),
                    Instruction = (ArgumentReader.ReadString(args, "instruction") ?? string.Empty).Trim(),
                    ScheduleType = ParseScheduleType(ArgumentReader.ReadString(args, "scheduleType")),
                    TimeOfDay = ArgumentReader.ReadString(args, "timeOfDay") ?? "08:00",
                    IntervalMinutes = ArgumentReader.ReadInt(args, "intervalMinutes") ?? 60,
                    WeekDays = ReadIntArray(args, "weekDays") ?? new[] { 1 },
                    MonthDays = ReadIntArray(args, "monthDays") ?? new[] { 1 },
                    ProviderId = NullIfBlank(ArgumentReader.ReadString(args, "providerId")),
                    AgentId = NullIfBlank(ArgumentReader.ReadString(args, "agentId")),
                    ReadOnly = ReadBool(args, "readOnly") ?? true,
                    TimeoutSeconds = ArgumentReader.ReadInt(args, "timeoutSeconds") ?? 1800,
                    TokenBudget = ArgumentReader.ReadInt(args, "tokenBudget") ?? 0,
                    NotifyOnlyOnIssue = ReadBool(args, "notifyOnlyOnIssue") ?? true,
                    Source = "AI",
                    RunOnStartupIfMissed = true,
                    OwnerUserId = "local"
                };

                if (task.ScheduleType == AiTaskScheduleType.Once)
                {
                    var onceText = ArgumentReader.ReadString(args, "onceAt");
                    if (DateTime.TryParse(onceText, out var once))
                        task.OnceAt = once;
                }

                if (string.IsNullOrWhiteSpace(task.Name))
                    task.Name = BuildName(task.Instruction);

                var errors = task.Validate();
                if (errors.Count > 0)
                    return "创建失败：" + string.Join("；", errors);

                task.NextRunAt = AiTaskSchedule.ComputeNextRun(task, DateTime.Now);
                var summary = BuildSummary(task);
                if (confirm is not null && !await confirm(task, summary, ct))
                    return "已取消创建。";

                await store.CreateAsync(task, ct);
                return $"已创建 AI 定时任务「{task.Name}」\n" +
                       $"- 调度：{task.DescribeSchedule()}\n" +
                       $"- 下次运行：{FormatDate(task.NextRunAt)}\n" +
                       $"- 模式：{(task.ReadOnly ? "只读" : "可写（执行时仍受审批/审计约束）")}\n" +
                       $"- 任务 Id：{task.Id}\n" +
                       "到点后会创建新会话执行，并通知结果。";
            });

        var list = new LocalFunction(
            ListTool,
            "列出当前所有 AI 定时任务（含启停状态、调度、下次运行、上次结果）。",
            """{"type":"object","properties":{"includeDisabled":{"type":"boolean","description":"是否包含已停用任务，默认 true"}}}""",
            async (args, ct) =>
            {
                var includeDisabled = ReadBool(args, "includeDisabled") ?? true;
                var tasks = await store.ListAsync(includeDisabled, ct);
                if (tasks.Count == 0)
                    return "当前没有 AI 定时任务。";
                var sb = new StringBuilder();
                sb.AppendLine($"共 {tasks.Count} 个 AI 定时任务：");
                foreach (var task in tasks)
                {
                    sb.AppendLine($"- {(task.Enabled ? "启用" : "停用")} | {task.Name} | {task.DescribeSchedule()} | " +
                                  $"下次 {FormatDate(task.NextRunAt)} | 上次 {task.LastResult ?? "无"} | Id {task.Id}");
                }
                return sb.ToString().TrimEnd();
            });

        var setEnabled = new LocalFunction(
            SetEnabledTool,
            "启用或停用 AI 定时任务。可用任务 Id 或名称指定。",
            """{"type":"object","properties":{"id":{"type":"string","description":"任务 Id 或名称"},"enabled":{"type":"boolean","description":"true=启用，false=停用"}},"required":["id","enabled"]}""",
            async (args, ct) =>
            {
                var key = ArgumentReader.ReadString(args, "id");
                var enabled = ReadBool(args, "enabled");
                if (string.IsNullOrWhiteSpace(key) || enabled is null)
                    return "缺少 id 或 enabled。";
                var task = await ResolveAsync(store, key, ct);
                if (task is null)
                    return $"未找到任务：{key}";
                if (confirm is not null &&
                    !await confirm(task, $"{(enabled.Value ? "启用" : "停用")} AI 定时任务「{task.Name}」？", ct))
                    return "已取消。";
                await store.SetEnabledAsync(task.Id, enabled.Value, ct);
                return $"已{(enabled.Value ? "启用" : "停用")}任务「{task.Name}」。";
            });

        var delete = new LocalFunction(
            DeleteTool,
            "删除 AI 定时任务（同时删除其运行历史）。可用任务 Id 或名称指定。",
            """{"type":"object","properties":{"id":{"type":"string","description":"任务 Id 或名称"}},"required":["id"]}""",
            async (args, ct) =>
            {
                var key = ArgumentReader.ReadString(args, "id");
                if (string.IsNullOrWhiteSpace(key))
                    return "缺少 id。";
                var task = await ResolveAsync(store, key, ct);
                if (task is null)
                    return $"未找到任务：{key}";
                if (confirm is not null &&
                    !await confirm(task, $"删除 AI 定时任务「{task.Name}」及其运行历史？此操作不可恢复。", ct))
                    return "已取消。";
                await store.DeleteAsync(task.Id, ct);
                return $"已删除任务「{task.Name}」。";
            });

        var runNow = new LocalFunction(
            RunNowTool,
            "请求立即运行一次 AI 定时任务（由桌面 App 调度器在下一轮领取执行）。可用任务 Id 或名称指定。",
            """{"type":"object","properties":{"id":{"type":"string","description":"任务 Id 或名称"}},"required":["id"]}""",
            async (args, ct) =>
            {
                var key = ArgumentReader.ReadString(args, "id");
                if (string.IsNullOrWhiteSpace(key))
                    return "缺少 id。";
                var task = await ResolveAsync(store, key, ct);
                if (task is null)
                    return $"未找到任务：{key}";
                if (confirm is not null && !await confirm(task, $"立即运行 AI 定时任务「{task.Name}」？", ct))
                    return "已取消。";
                await store.RequestRunAsync(task.Id, ct);
                return $"已请求立即运行「{task.Name}」，App 调度器将在下一轮（约 20 秒内）执行。";
            });

        var runs = new LocalFunction(
            RunsTool,
            "查看某个 AI 定时任务的最近运行历史。可用任务 Id 或名称指定。",
            """{"type":"object","properties":{"id":{"type":"string","description":"任务 Id 或名称"},"limit":{"type":"integer","description":"返回条数，默认 10"}},"required":["id"]}""",
            async (args, ct) =>
            {
                var key = ArgumentReader.ReadString(args, "id");
                if (string.IsNullOrWhiteSpace(key))
                    return "缺少 id。";
                var task = await ResolveAsync(store, key, ct);
                if (task is null)
                    return $"未找到任务：{key}";
                var limit = ArgumentReader.ReadInt(args, "limit") ?? 10;
                var history = await store.ListRunsAsync(task.Id, limit, ct);
                if (history.Count == 0)
                    return $"任务「{task.Name}」还没有运行记录。";
                var sb = new StringBuilder();
                sb.AppendLine($"任务「{task.Name}」最近 {history.Count} 次运行：");
                foreach (var run in history)
                {
                    sb.AppendLine($"- {run.StartedAt:yyyy-MM-dd HH:mm} | {run.Status} | 触发 {run.Trigger} | " +
                                  $"耗时 {FormatDuration(run)} | token {run.InputTokens + run.OutputTokens}" +
                                  (string.IsNullOrWhiteSpace(run.Summary) ? string.Empty : $" | {Truncate(run.Summary, 120)}"));
                }
                return sb.ToString().TrimEnd();
            });

        return new IAgentTool[]
        {
            new LocalAgentTool(create),
            new LocalAgentTool(list, readOnly: true),
            new LocalAgentTool(setEnabled),
            new LocalAgentTool(delete),
            new LocalAgentTool(runNow),
            new LocalAgentTool(runs, readOnly: true)
        };
    }

    private static async Task<AiTaskConfig?> ResolveAsync(AiTaskStore store, string idOrName, CancellationToken ct)
    {
        var task = await store.GetAsync(idOrName, ct);
        return task ?? await store.FindByNameAsync(idOrName, ct);
    }

    private static string BuildName(string instruction)
    {
        var text = instruction.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length == 0)
            return "AI 定时任务";
        return text.Length <= 20 ? text : text[..20] + "…";
    }

    private static string BuildSummary(AiTaskConfig task)
    {
        var sb = new StringBuilder();
        sb.AppendLine("即将创建 AI 定时任务：");
        sb.AppendLine($"名称：{task.Name}");
        sb.AppendLine($"调度：{task.DescribeSchedule()}");
        sb.AppendLine($"模式：{(task.ReadOnly ? "只读" : "可写（执行时仍受审批/审计约束）")}");
        sb.AppendLine($"超时：{task.TimeoutSeconds} 秒");
        if (task.TokenBudget > 0)
            sb.AppendLine($"token 预算：{task.TokenBudget}");
        sb.AppendLine();
        sb.AppendLine("任务指令：");
        sb.AppendLine(Truncate(task.Instruction, 500));
        return sb.ToString().TrimEnd();
    }

    private static AiTaskScheduleType ParseScheduleType(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return text switch
        {
            "once" or "one-time" or "一次性" => AiTaskScheduleType.Once,
            "interval" or "every" or "间隔" => AiTaskScheduleType.Interval,
            "daily" or "每天" or "每日" => AiTaskScheduleType.Daily,
            "weekly" or "每周" => AiTaskScheduleType.Weekly,
            "monthly" or "每月" => AiTaskScheduleType.Monthly,
            _ => Enum.TryParse<AiTaskScheduleType>(text, ignoreCase: true, out var parsed)
                ? parsed
                : AiTaskScheduleType.Daily
        };
    }

    private static int[]? ReadIntArray(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is int[] array)
            return array;
        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
                return element.EnumerateArray()
                    .Select(item => item.TryGetInt32(out var n) ? n : int.MinValue)
                    .Where(n => n != int.MinValue)
                    .ToArray();
            if (element.ValueKind == JsonValueKind.String)
                return ParseIntList(element.GetString());
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var single))
                return new[] { single };
        }

        return ParseIntList(value.ToString());
    }

    private static int[]? ParseIntList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var parts = text.Split(new[] { ',', '，', ';', '；', ' ', '、' }, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<int>();
        foreach (var part in parts)
        {
            if (int.TryParse(part.Trim(), out var n))
                list.Add(n);
        }
        return list.Count > 0 ? list.ToArray() : null;
    }

    private static bool? ReadBool(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement je => bool.TryParse(je.ToString(), out var parsed) ? parsed : null,
            string s => bool.TryParse(s, out var parsed) ? parsed : null,
            _ => null
        };
    }

    private static string Trim(string? value) => (value ?? string.Empty).Trim();

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatDate(DateTime? value) =>
        value is null ? "无" : value.Value.ToString("yyyy-MM-dd HH:mm");

    private static string FormatDuration(AiTaskRun run) =>
        run.CompletedAt is null ? "运行中" : $"{(run.CompletedAt.Value - run.StartedAt).TotalSeconds:F1}s";

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}
