// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + mcp_usage_guide 内置清单(由注解反射生成, 无需手改);
//   ③ 若新增了工具类, 记得在 Program.cs 注册 WithTools<T>()。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。详见 docs/TOOLS.md 顶部"同步约定"。
using System.ComponentModel;
using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

public sealed class JobStartDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? JobId { get; set; }
    public string? Operation { get; set; }

    /// <summary>pending/running（wait=false 或等待被取消）/ succeeded/failed/cancelled/orphaned（wait=true 完成时）。</summary>
    public string? State { get; set; }

    /// <summary>job_start 完整结构化结果（仅 wait=true 且终态时非空）。</summary>
    public JsonElement? Result { get; set; }

    public static JobStartDto Fail(string status, string error) =>
        new() { Success = false, Status = status, Error = error };
}

public sealed class JobStatusDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? JobId { get; set; }
    public string? Operation { get; set; }
    public string? State { get; set; }
    public double Progress { get; set; }
    public int ProgressDone { get; set; }
    public int ProgressTotal { get; set; }
    public bool CancelRequested { get; set; }
    public bool Partial { get; set; }
    public string? ErrorStatus { get; set; }
    public string? ErrorText { get; set; }
    public string? CreatedAt { get; set; }
    public string? FinishedAt { get; set; }
    public List<JobEventDto>? RecentEvents { get; set; }

    public static JobStatusDto Fail(string status, string error) =>
        new() { Success = false, Status = status, Error = error };
}

public sealed class JobEventDto
{
    public long Seq { get; set; }
    public string Kind { get; set; } = "";

    /// <summary>纯文本事件消息；结构化事件在 <see cref="Data"/>，两者不会重复。</summary>
    public string? Message { get; set; }

    /// <summary>结构化事件数据（进度/阶段等）；普通文本事件为 null。</summary>
    public JsonElement? Data { get; set; }

    /// <summary>结构化列：目标服务器（进度事件）。</summary>
    public string? Server { get; set; }

    /// <summary>结构化列：阶段。</summary>
    public string? Phase { get; set; }

    /// <summary>结构化列：已完成数。</summary>
    public int? Done { get; set; }

    /// <summary>结构化列：总数。</summary>
    public int? Total { get; set; }

    /// <summary>结构化列：状态。</summary>
    public string? Status { get; set; }

    public string CreatedAt { get; set; } = "";
}

public sealed class JobResultDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? JobId { get; set; }
    public string? Operation { get; set; }
    public string? State { get; set; }
    public bool Partial { get; set; }

    /// <summary>完整结构化结果（与同步工具的结构化输出等价）；summary=true 时为空。</summary>
    public JsonElement? Result { get; set; }

    /// <summary>结果摘要（summary=true 时返回）；不包含完整 payload。</summary>
    public JobResultSummaryDto? Summary { get; set; }

    /// <summary>完整结果字符数；便于判断是否需要 summary。</summary>
    public int? ResultChars { get; set; }

    /// <summary>获取完整结果的提示（summary 模式）。</summary>
    public string? ResultHint { get; set; }

    public static JobResultDto Fail(string status, string error) =>
        new() { Success = false, Status = status, Error = error };
}

public sealed class JobResultSummaryDto
{
    public string Kind { get; set; } = "null";
    public int? ItemCount { get; set; }
    public int? FailedCount { get; set; }
    public int? ServerCount { get; set; }
    public List<string>? TopLevelKeys { get; set; }
    public int ResultChars { get; set; }
}

public sealed class JobEventsDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? JobId { get; set; }
    public long FromSeq { get; set; }
    public List<JobEventDto> Events { get; set; } = new();

    public static JobEventsDto Fail(string status, string error) =>
        new() { Success = false, Status = status, Error = error };
}

public sealed class JobCancelDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? JobId { get; set; }
    public string? State { get; set; }

    public static JobCancelDto Fail(string status, string error) =>
        new() { Success = false, Status = status, Error = error };
}

public sealed class JobListItemDto
{
    public string JobId { get; set; } = "";
    public string Operation { get; set; } = "";
    public string State { get; set; } = "";
    public double Progress { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? ErrorStatus { get; set; }
}

public sealed class JobListDto
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public List<JobListItemDto> Jobs { get; set; } = new();
    public List<string>? AvailableOperations { get; set; }

    public static JobListDto Fail(string status, string error) =>
        new() { Success = false, Status = status, Error = error };
}

/// <summary>
/// 长任务统一 Job 工具面（设计：maybe-to/2026-11-13-长任务统一Job模型设计.md）。
/// job_start 的授权按 operation 参数路由到目标工具的安全描述符（方案A）；
/// 校验/审批在 job_start 请求内同步完成，后台执行不再有交互式闸门。
/// </summary>
[McpServerToolType]
public class JobTools
{
    private readonly JobManager _jobManager;
    private readonly SqliteJobStore _store;
    private readonly McpSessionTracker _session;
    private readonly IAuditLogService _auditLogService;

    public JobTools(JobManager jobManager, SqliteJobStore store, McpSessionTracker session,
        IAuditLogService auditLogService)
    {
        _jobManager = jobManager;
        _store = store;
        _session = session;
        _auditLogService = auditLogService;
    }

    private static string CurrentUser => AuditContext.CurrentUserId ?? "local";

    private static bool IsAdmin =>
        string.Equals(Environment.GetEnvironmentVariable(HostEnv.AuthzRole), HostEnv.AdminRole, StringComparison.Ordinal);

    [McpServerTool(Name = "job_start", UseStructuredContent = true, OutputSchemaType = typeof(JobStartDto), Destructive = false, ReadOnly = false, Idempotent = false, OpenWorld = true)]
    [Description("启动长任务；多机用 fleet_*；wait=false 返回 jobId。")]
    public async Task<JobStartDto> Start(
        [Description("长任务 operation 名；可用值见 mcp_usage_guide 的 job_operations")] string operation,
        [Description("旧版 JSON 字符串参数；兼容保留，优先用 argumentsObject")] string? arguments = null,
        [Description("结构化参数对象（推荐），如 {\"serverIds\":\"web-1,db-1\"}")] JsonElement? argumentsObject = null,
        [Description("是否同步等待完成(默认 false=后台执行立即返回 jobId)")] bool wait = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var argumentsJson = argumentsObject is { } structured
            ? structured.ValueKind == JsonValueKind.String
                ? structured.GetString() ?? string.Empty
                : structured.GetRawText()
            : arguments ?? string.Empty;
        var outcome = await _jobManager.StartAsync(
            operation, argumentsJson, CurrentUser, _session.SessionId, wait,
            wait ? progress : null, cancellationToken);

        if (!outcome.Accepted)
            return JobStartDto.Fail(outcome.ErrorStatus ?? "start_failed", outcome.ErrorText ?? "任务启动失败");

        await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
        {
            ServerId = "jobs",
            ServerName = "jobs",
            Command = $"JOB_START: {operation} {Truncate(argumentsJson, 200)}",
            Result = outcome.Completed ? $"完成 state={outcome.State}" : $"后台执行 {outcome.JobId}",
            Status = CommandStatus.Executed,
            Category = AuditCategory.Gate
        });

        return new JobStartDto
        {
            Success = true,
            Status = outcome.Completed ? outcome.State : "running",
            JobId = outcome.JobId,
            Operation = operation,
            State = outcome.Completed ? outcome.State : JobStates.Running,
            Result = ParseResult(outcome.ResultJson),
            Error = outcome.Completed && outcome.State != JobStates.Succeeded ? outcome.ErrorText : null
        };
    }

    [McpServerTool(Name = "job_status", UseStructuredContent = true, OutputSchemaType = typeof(JobStatusDto), ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("查询任务状态与进度。")]
    public async Task<JobStatusDto> Status(
        [Description("job_start 返回的任务 ID")] string jobId,
        [Description("附带最近 N 条事件(默认 5, 0=不带)")] int includeEvents = 5,
        CancellationToken cancellationToken = default)
    {
        var (job, error) = await LoadAccessibleAsync(jobId, cancellationToken);
        if (job is null) return JobStatusDto.Fail(error.status, error.error);

        var dto = new JobStatusDto
        {
            Success = true,
            JobId = job.Id,
            Operation = job.Operation,
            State = job.State,
            Progress = job.Progress,
            ProgressDone = job.ProgressDone,
            ProgressTotal = job.ProgressTotal,
            CancelRequested = job.CancelRequested,
            Partial = job.Partial,
            ErrorStatus = job.ErrorStatus,
            ErrorText = job.ErrorText,
            CreatedAt = job.CreatedAt.ToString("O"),
            FinishedAt = job.FinishedAt?.ToString("O")
        };

        if (includeEvents > 0)
        {
            // 最近 N 条（按 Seq 升序），不是最早 N 条
            var events = await _store.GetRecentEventsAsync(jobId, Math.Clamp(includeEvents, 1, 50), cancellationToken);
            dto.RecentEvents = events.Select(ToEventDto).ToList();
        }
        return dto;
    }

    [McpServerTool(Name = "job_result", UseStructuredContent = true, OutputSchemaType = typeof(JobResultDto), ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("获取终态结果。")]
    public async Task<JobResultDto> Result(
        [Description("job_start 返回的任务 ID")] string jobId,
        [Description("只返回摘要（数量/失败数/顶层字段），不返回完整 payload")] bool summary = false,
        CancellationToken cancellationToken = default)
    {
        var (job, error) = await LoadAccessibleAsync(jobId, cancellationToken);
        if (job is null) return JobResultDto.Fail(error.status, error.error);

        if (!JobStates.IsTerminal(job.State))
            return new JobResultDto
            {
                Success = true,
                Status = "running",
                JobId = job.Id,
                Operation = job.Operation,
                State = job.State,
                Error = $"任务仍在{job.State}, 进度 {job.ProgressDone}/{job.ProgressTotal}; 请稍后重试或用 job_status 轮询"
            };

        var result = ParseResult(job.ResultJson);
        var resultChars = job.ResultJson?.Length ?? 0;
        return new JobResultDto
        {
            Success = job.State == JobStates.Succeeded,
            Status = job.State,
            JobId = job.Id,
            Operation = job.Operation,
            State = job.State,
            Partial = job.Partial,
            Result = summary ? null : result,
            Summary = summary ? BuildResultSummary(result, resultChars) : null,
            ResultChars = resultChars,
            ResultHint = summary && result is not null
                ? $"完整结果请调用 job_result(jobId=\"{job.Id}\", summary=false) 获取"
                : null,
            Error = job.State == JobStates.Succeeded ? null : job.ErrorText
        };
    }

    [McpServerTool(Name = "job_events", UseStructuredContent = true, OutputSchemaType = typeof(JobEventsDto), ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("断点续读任务事件。")]
    public async Task<JobEventsDto> Events(
        [Description("job_start 返回的任务 ID")] string jobId,
        [Description("从该序号之后开始读(首次传 0, 续传传上次最大 Seq)")] long fromSeq = 0,
        [Description("最多返回条数(默认 100, 1~500)")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var (job, error) = await LoadAccessibleAsync(jobId, cancellationToken);
        if (job is null) return JobEventsDto.Fail(error.status, error.error);

        var events = await _store.GetEventsAsync(jobId, fromSeq, Math.Clamp(limit, 1, 500), cancellationToken);
        return new JobEventsDto
        {
            Success = true,
            JobId = jobId,
            FromSeq = fromSeq,
            Events = events.Select(ToEventDto).ToList()
        };
    }

    [McpServerTool(Name = "job_cancel", UseStructuredContent = true, OutputSchemaType = typeof(JobCancelDto), Destructive = false, ReadOnly = false, Idempotent = false, OpenWorld = false)]
    [Description("取消长任务，保留部分事件。")]
    public async Task<JobCancelDto> Cancel(
        [Description("job_start 返回的任务 ID")] string jobId,
        CancellationToken cancellationToken = default)
    {
        var (job, error) = await LoadAccessibleAsync(jobId, cancellationToken);
        if (job is null) return JobCancelDto.Fail(error.status, error.error);

        if (JobStates.IsTerminal(job.State))
            return new JobCancelDto
            {
                Success = true,
                Status = "already_finished",
                JobId = jobId,
                State = job.State,
                Error = $"任务已处于终态({job.State}), 无需取消"
            };

        var cancelled = await _jobManager.CancelAsync(jobId);
        return new JobCancelDto
        {
            Success = cancelled,
            Status = cancelled ? "cancel_requested" : "cancel_failed",
            JobId = jobId,
            State = cancelled ? "cancelling" : job.State,
            Error = cancelled ? null : "取消请求失败(任务可能刚结束)"
        };
    }

    [McpServerTool(Name = "job_list", UseStructuredContent = true, OutputSchemaType = typeof(JobListDto), ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("列出本人近期长任务。")]
    public async Task<JobListDto> List(
        [Description("按 operation 过滤(可选)")] string? operation = null,
        [Description("按状态过滤: pending/running/succeeded/failed/cancelled/orphaned(可选)")] string? state = null,
        [Description("最多返回条数(默认 20, 1~100)")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (operation is not null && !_jobManager.IsRegistered(operation))
            return JobListDto.Fail("unknown_operation",
                $"未注册的 operation: {operation}。可用: {string.Join(", ", _jobManager.ListOperations().Select(o => o.Operation))}");

        var jobs = await _store.ListAsync(CurrentUser, operation, state, Math.Clamp(limit, 1, 100), cancellationToken);
        return new JobListDto
        {
            Success = true,
            Jobs = jobs.Select(j => new JobListItemDto
            {
                JobId = j.Id,
                Operation = j.Operation,
                State = j.State,
                Progress = j.Progress,
                CreatedAt = j.CreatedAt.ToString("O"),
                ErrorStatus = j.ErrorStatus
            }).ToList(),
            AvailableOperations = operation is null
                ? _jobManager.ListOperations().Select(o => $"{o.Operation} - {o.Description}").ToList()
                : null
        };
    }

    /// <summary>
    /// 加载并做可见性校验：非本人且非管理员 → not_found（不泄露存在性）。
    /// 刻意只按 OwnerUser 隔离（不校验 SessionId）：G2 要求任一 MCP 子进程可查询/取消其他进程
    /// 启动的 job，而 SessionId 是进程级 ID，严格校验会让跨进程可见性失效。管理员可跨用户查看。
    /// </summary>
    private async Task<(JobState? Job, (string status, string error) Error)> LoadAccessibleAsync(
        string jobId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return (null, ("invalid_job_id", "jobId 不能为空"));

        var job = await _store.GetAsync(jobId, ct);
        if (job is null)
            return (null, ("job_not_found", $"任务不存在或无权访问: {jobId}"));

        if (!IsAdmin && !string.Equals(job.OwnerUser, CurrentUser, StringComparison.Ordinal))
            return (null, ("job_not_found", $"任务不存在或无权访问: {jobId}"));

        return (job, (null!, null!));
    }

    private static JobEventDto ToEventDto(JobEvent e)
    {
        var dto = new JobEventDto
        {
            Seq = e.Seq,
            Kind = e.Kind,
            Server = string.IsNullOrWhiteSpace(e.Server) ? null : e.Server,
            Phase = string.IsNullOrWhiteSpace(e.Phase) ? null : e.Phase,
            Done = e.Done,
            Total = e.Total,
            Status = string.IsNullOrWhiteSpace(e.Status) ? null : e.Status,
            CreatedAt = e.CreatedAt.ToString("O")
        };
        if (TryParseStructured(e.Message, out var data))
            dto.Data = data;
        else
            dto.Message = e.Message;
        return dto;
    }

    private static JobResultSummaryDto BuildResultSummary(JsonElement? result, int resultChars)
    {
        var summary = new JobResultSummaryDto { ResultChars = resultChars };
        if (result is not { } root)
            return summary;

        summary.Kind = root.ValueKind switch
        {
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            _ => "null"
        };

        if (root.ValueKind == JsonValueKind.Object)
        {
            summary.TopLevelKeys = root.EnumerateObject().Select(p => p.Name).Take(20).ToList();
            foreach (var key in new[] { "servers", "results", "items", "jobs" })
            {
                if (!root.TryGetProperty(key, out var array) || array.ValueKind != JsonValueKind.Array)
                    continue;
                summary.ItemCount = array.GetArrayLength();
                if (key is "servers" or "results")
                    summary.ServerCount = array.GetArrayLength();
                summary.FailedCount = array.EnumerateArray().Count(IsFailedItem);
                break;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            summary.ItemCount = root.GetArrayLength();
            summary.FailedCount = root.EnumerateArray().Count(IsFailedItem);
        }

        return summary;
    }

    private static bool IsFailedItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return false;
        if (item.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(error.GetString()))
            return true;
        if (item.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            return true;
        if (item.TryGetProperty("online", out var online) && online.ValueKind == JsonValueKind.False)
            return true;
        if (item.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
            return status.GetString() is "failed" or "error" or "offline";
        return false;
    }

    private static JsonElement? ParseResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(json);
        }
    }

    private static bool TryParseStructured(string? text, out JsonElement data)
    {
        data = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            data = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? value : (value.Length <= max ? value : value[..max] + "…");
}
