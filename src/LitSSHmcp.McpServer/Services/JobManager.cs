using System.Collections.Concurrent;
using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;
using ModelContextProtocol;

namespace LitSSHmcp.McpServer.Services;

/// <summary>
/// 长任务 Job 执行器（详见 maybe-to/2026-11-13-长任务统一Job模型设计.md §3）。
/// 进程级单例：注册 operation、创建/执行 Job、桥接进度到 MCP progress notification、
/// 观察跨进程取消标记、刷新心跳；进程退出时把本进程 running Job 标记 orphaned。
/// </summary>
public sealed class JobManager : IAsyncDisposable
{
    /// <summary>Prepare 阶段结果：校验/审批通过后携带待执行委托；否则携带错误。</summary>
    public sealed class JobPrepared
    {
        public bool Ok { get; private init; }
        public string? ErrorStatus { get; private init; }
        public string? ErrorText { get; private init; }
        public Func<JobExecutionContext, CancellationToken, Task<JobOutcome>>? Runner { get; private init; }

        public static JobPrepared Ready(Func<JobExecutionContext, CancellationToken, Task<JobOutcome>> runner) =>
            new() { Ok = true, Runner = runner };

        public static JobPrepared Fail(string status, string error) =>
            new() { Ok = false, ErrorStatus = status, ErrorText = error };
    }

    /// <summary>runner 的最终结果：Success 时 Result 序列化为 ResultJson。</summary>
    public sealed class JobOutcome
    {
        public bool Success { get; private init; }
        public object? Result { get; private init; }
        public string? ErrorStatus { get; private init; }
        public string? ErrorText { get; private init; }

        public static JobOutcome Succeed(object? result = null) => new() { Success = true, Result = result };
        public static JobOutcome Fail(string status, string error) =>
            new() { Success = false, ErrorStatus = status, ErrorText = error };
    }

    /// <summary>Prepare 委托：在 job_start 请求内同步完成校验与审批（G5：闸门前置）。</summary>
    public delegate Task<JobPrepared> JobPrepare(JsonElement arguments, CancellationToken ct);

    /// <summary>Job 执行上下文：事件上报、进度上报（自动落库+桥接 MCP progress+刷新心跳）。</summary>
    public sealed class JobExecutionContext
    {
        private readonly JobManager _manager;
        private readonly IProgress<ProgressNotificationValue>? _mcpProgress;

        internal string JobId { get; }
        internal string Operation { get; }

        internal JobExecutionContext(JobManager manager, string jobId, string operation,
            IProgress<ProgressNotificationValue>? mcpProgress)
        {
            _manager = manager;
            JobId = jobId;
            Operation = operation;
            _mcpProgress = mcpProgress;
        }

        /// <summary>追加一条事件（落库 + 桥接 MCP progress + 刷新心跳）。上报失败不影响执行主流程。</summary>
        public async Task EmitAsync(string kind, string message, CancellationToken ct = default)
        {
            try
            {
                await _manager._store.AppendEventAsync(JobId, kind, message, ct);
                await _manager._store.TouchHeartbeatAsync(JobId, ct);
                _mcpProgress?.Report(new ProgressNotificationValue { Progress = 0, Message = message });
            }
            catch { /* 事件/进度失败不阻断任务 */ }
        }

        /// <summary>更新数值进度（done/total，自动换算百分比；落库 + 桥接 MCP progress + 追加事件）。</summary>
        public Task ReportProgressAsync(int done, int total, string? message = null, CancellationToken ct = default) =>
            ReportProgressAsync(JobEventKinds.Progress, done, total, message, ct);

        /// <summary>更新数值进度并追加指定类型的事件（phase/progress），供 job_events 断点续读。</summary>
        public async Task ReportProgressAsync(string kind, int done, int total, string? message = null, CancellationToken ct = default)
        {
            try
            {
                var pct = total > 0 ? Math.Clamp((double)done / total, 0, 1) * 100 : 0;
                await _manager._store.UpdateProgressAsync(JobId, Math.Max(0, done), Math.Max(0, total), pct, ct);
                if (!string.IsNullOrWhiteSpace(message))
                    await _manager._store.AppendEventAsync(JobId, kind, message, ct);
                if (_mcpProgress is not null && total > 0)
                    _mcpProgress.Report(new ProgressNotificationValue
                    {
                        Progress = Math.Max(0, done),
                        Total = Math.Max(0, total),
                        Message = message
                    });
            }
            catch { /* 进度失败不阻断任务 */ }
        }

        /// <summary>
        /// 把任意 T 进度适配为 IProgress&lt;T&gt;（message 由 format 委托生成稳定 JSON）；
        /// kind 选择器可区分 phase/progress，缺省为 progress。写入按顺序排队，终态前会 flush。
        /// </summary>
        public IProgress<T> AsProgress<T>(Func<T, (int done, int total, string message)> format,
            Func<T, string>? kind = null) =>
            new CallbackProgress<T>(value =>
            {
                var (done, total, message) = format(value);
                var eventKind = kind?.Invoke(value) ?? JobEventKinds.Progress;
                _manager.EnqueueProgress(() => ReportProgressAsync(eventKind, done, total, message));
            });

        internal Task FlushProgressAsync() => _manager.FlushProgressAsync();

        private sealed class CallbackProgress<T>(Action<T> handler) : IProgress<T>
        {
            public void Report(T value) => handler(value);
        }
    }

    private sealed record JobRegistration(string Operation, string Description, string UnderlyingTool,
        ToolSecurityDescriptor RequestDescriptor, JobPrepare Prepare);

    private readonly SqliteJobStore _store;
    private readonly ConcurrentDictionary<string, JobRegistration> _operations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningJobs = new(StringComparer.Ordinal);
    private readonly IConfigService _configService;
    private readonly object _progressGate = new();
    private Task _progressTail = Task.CompletedTask;
    private DateTimeOffset _lastCleanupAt = DateTimeOffset.MinValue;
    private volatile bool _disposed;

    public JobManager(SqliteJobStore store, IConfigService configService)
    {
        _store = store;
        _configService = configService;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => MarkOwnJobsOrphaned();
    }

    /// <summary>是否已注册该 operation（授权过滤器 fail-closed 判定用）。</summary>
    public bool IsRegistered(string operation) => _operations.ContainsKey(operation);

    public IReadOnlyList<(string Operation, string Description)> ListOperations() =>
        _operations.Values.Select(r => (r.Operation, r.Description)).OrderBy(r => r.Operation).ToArray();

    /// <summary>查询某个 operation 当前 running 的 job（跨进程，取最新一条），供同步入口提示 jobId/进度。</summary>
    public async Task<JobState?> GetRunningJobAsync(string operation, CancellationToken ct = default)
    {
        var jobs = await _store.ListAsync(null, operation, JobStates.Running, 1, ct);
        return jobs.FirstOrDefault();
    }

    /// <summary>测试/兼容便捷重载：不校验底层安全描述符；真实 operation 请用带 underlyingTool 的重载。</summary>
    public void Register(string operation, string description, JobPrepare prepare) =>
        _operations[operation] = new JobRegistration(operation, description, operation,
            new ToolSecurityDescriptor(operation, ToolTargetKind.None, Array.Empty<string>(),
                ToolAccessLevel.Read, false, false), prepare);

    /// <summary>
    /// 注册 operation 及其底层工具描述符。job_start 的安全路由、授权、写锁均从本注册表读取，
    /// 不再维护第二份 operation 映射。重复注册后者覆盖。
    /// </summary>
    public void Register(string operation, string description, string underlyingTool, JobPrepare prepare)
    {
        var descriptor = ToolSecurityCatalog.Find(underlyingTool)
            ?? throw new InvalidOperationException($"Job operation '{operation}' 的底层工具 '{underlyingTool}' 未登记安全描述符");
        _operations[operation] = new JobRegistration(operation, description, underlyingTool, descriptor, prepare);
    }

    /// <summary>注册 job-only operation：没有同名 MCP 工具时，直接提供 job_start 授权用的目标描述符。</summary>
    public void Register(string operation, string description, ToolSecurityDescriptor requestDescriptor,
        JobPrepare prepare) =>
        _operations[operation] = new JobRegistration(operation, description, requestDescriptor.Name,
            requestDescriptor, prepare);

    /// <summary>job_start 安全路由：operation → 底层工具名（兼容/诊断用）。</summary>
    public bool TryGetUnderlyingTool(string operation, out string underlyingTool)
    {
        if (_operations.TryGetValue(operation, out var registration))
        {
            underlyingTool = registration.UnderlyingTool;
            return true;
        }

        underlyingTool = string.Empty;
        return false;
    }

    /// <summary>job_start 安全路由：operation → 本次调用应使用的安全描述符。</summary>
    public bool TryGetRequestDescriptor(string operation, out ToolSecurityDescriptor descriptor)
    {
        if (_operations.TryGetValue(operation, out var registration))
        {
            descriptor = registration.RequestDescriptor;
            return true;
        }

        descriptor = null!;
        return false;
    }

    private void EnqueueProgress(Func<Task> work)
    {
        lock (_progressGate)
        {
            _progressTail = _progressTail.ContinueWith(
                async _ =>
                {
                    try { await work(); }
                    catch { /* 进度写入失败不阻断任务 */ }
                }, TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>终态落库前等待所有通过 AsProgress 排队的进度事件写完，保证事件顺序。</summary>
    internal Task FlushProgressAsync()
    {
        lock (_progressGate)
            return _progressTail;
    }

    /// <summary>启动 Job：Prepare（校验+审批）同步执行；执行阶段后台运行。</summary>
    /// <param name="progress">wait=true 时桥接 MCP progress notification；wait=false 传 null。</param>
    public async Task<JobStartOutcome> StartAsync(
        string operation,
        string argumentsJson,
        string ownerUser,
        string sessionId,
        bool wait,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken ct = default)
    {
        if (!_operations.TryGetValue(operation, out var registration))
            return JobStartOutcome.Fail("unknown_operation",
                $"未注册的长任务 operation: {operation}。可用: {string.Join(", ", _operations.Keys.OrderBy(k => k))}。");

        JsonElement arguments;
        try
        {
            arguments = string.IsNullOrWhiteSpace(argumentsJson)
                ? JsonSerializer.SerializeToElement(new { })
                : JsonDocument.Parse(argumentsJson).RootElement;
            if (arguments.ValueKind != JsonValueKind.Object)
                return JobStartOutcome.Fail("invalid_arguments", "arguments 必须是 JSON 对象文本，如 {\"serverIds\":\"a,b\"}。");
        }
        catch (JsonException ex)
        {
            return JobStartOutcome.Fail("invalid_arguments", $"arguments 不是合法 JSON: {ex.Message}");
        }

        // 闸门前置（G5）：路径策略、命令过滤、人工审批全部在 job_start 请求内完成
        var prepared = await registration.Prepare(arguments, ct);
        if (!prepared.Ok)
            return JobStartOutcome.Fail(prepared.ErrorStatus ?? "prepare_failed", prepared.ErrorText ?? "任务未通过启动前校验");

        // 滚动清理（节流 5 分钟一次，fire-and-forget 不阻塞启动）：orphan 标记 + TTL + 单用户上限
        if (DateTimeOffset.UtcNow - _lastCleanupAt > TimeSpan.FromMinutes(5))
        {
            _lastCleanupAt = DateTimeOffset.UtcNow;
            _ = Task.Run(async () =>
            {
                try
                {
                    var config = await _configService.LoadConfigAsync();
                    var jobsConfig = config.Security?.Jobs ?? new JobsConfig();
                    await _store.CleanupAsync(jobsConfig.OrphanHeartbeatSeconds,
                        jobsConfig.RetentionHours, jobsConfig.MaxFinishedPerUser);
                }
                catch { /* 清理失败不影响本次任务 */ }
            });
        }

        var job = new JobState
        {
            Id = $"job_{Guid.NewGuid().ToString("N")[..12]}",
            Operation = operation,
            ArgumentsJson = arguments.GetRawText(),
            State = JobStates.Running,
            OwnerUser = ownerUser,
            SessionId = sessionId,
            ProcessId = Environment.ProcessId,
            HeartbeatAt = DateTimeOffset.UtcNow
        };
        await _store.SaveAsync(job, ct);

        // 后台执行：取消令牌 = 跨进程 cancel_requested 轮询 + 本地 CTS（job_cancel/进程退出可触发）
        var cts = new CancellationTokenSource();
        _runningJobs[job.Id] = cts;
        var runner = prepared.Runner!;

        var background = Task.Run(async () =>
        {
            using var cancelPoller = new System.Threading.Timer(_ =>
            {
                try
                {
                    if (_disposed) return;
                    var (state, cancelRequested) = _store.PeekFlagsAsync(job.Id).GetAwaiter().GetResult();
                    if (cancelRequested && state == JobStates.Running)
                        cts.Cancel();
                }
                catch { /* 轮询失败不终止任务 */ }
            }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

            var context = new JobExecutionContext(this, job.Id, operation, progress);
            JobOutcome outcome;
            try
            {
                outcome = await runner(context, cts.Token);
            }
            catch (OperationCanceledException)
            {
                outcome = JobOutcome.Fail("cancelled", "任务已被取消（已产生的部分事件保留，可 job_events 查看）");
            }
            catch (DiscoveryInProgressException ex)
            {
                outcome = JobOutcome.Fail("discovery_in_progress", ex.Message);
            }
            catch (Exception ex)
            {
                outcome = JobOutcome.Fail("exception", ex.Message);
            }
            finally
            {
                _runningJobs.TryRemove(job.Id, out _);
            }

            // 先 flush AsProgress 排队的进度事件，再写终态/result，保证事件顺序。
            try { await context.FlushProgressAsync(); } catch { /* 进度写入失败不阻断终态 */ }

            try
            {
                var final = await _store.GetAsync(job.Id) ?? job;
                final.State = outcome.Success ? JobStates.Succeeded
                    : outcome.ErrorStatus == "cancelled" ? JobStates.Cancelled
                    : JobStates.Failed;
                final.ErrorStatus = outcome.Success ? null : outcome.ErrorStatus;
                final.ErrorText = outcome.Success ? null : outcome.ErrorText;
                final.Partial = final.State == JobStates.Cancelled;
                final.FinishedAt = DateTimeOffset.UtcNow;
                if (outcome.Success && outcome.Result is not null)
                    final.ResultJson = JsonSerializer.Serialize(outcome.Result, JsonOutput.Options);
                // 先写 result 事件，保证终态/job_status 读终态时从 job_events 一定能读到 result
                await _store.AppendEventAsync(job.Id, JobEventKinds.Result,
                    JsonSerializer.Serialize(new { state = final.State, errorStatus = final.ErrorStatus },
                        JsonOutput.Options), CancellationToken.None);
                await _store.SaveAsync(final, CancellationToken.None);
            }
            catch { /* 终态落库失败不重抛（后台任务） */ }
        }, CancellationToken.None);

        if (!wait)
            return JobStartOutcome.Started(job.Id);

        // wait=true：同步等待完成（小任务友好，等同现状行为），等待期间事件经 progress 实时上报
        var completed = await Task.WhenAny(background, Task.Delay(Timeout.Infinite, ct));
        if (completed != background)
        {
            // 调用方取消：任务继续在后台跑，返回 jobId 供后续 job_result 收割
            return JobStartOutcome.Started(job.Id, "等待被取消，任务仍在后台执行");
        }

        var final = await _store.GetAsync(job.Id, ct) ?? job;
        return new JobStartOutcome
        {
            Accepted = true,
            JobId = job.Id,
            Completed = true,
            State = final.State,
            ResultJson = final.ResultJson,
            ErrorStatus = final.ErrorStatus,
            ErrorText = final.ErrorText
        };
    }

    /// <summary>取消（job_cancel 工具调用）：无论是否本进程都先落取消标记，本进程再触发 CTS 立即停止。</summary>
    public async Task<bool> CancelAsync(string jobId)
    {
        var job = await _store.GetAsync(jobId);
        if (job is null || JobStates.IsTerminal(job.State))
            return false;

        // 跨进程取消标记 + 本地快速取消都会写库，保证 job_status 能看到 cancelRequested
        await _store.SetCancelRequestedAsync(jobId);
        if (_runningJobs.TryGetValue(jobId, out var cts))
            cts.Cancel();
        return true;
    }

    /// <summary>当前本进程 running 的 Job 数（诊断用）。</summary>
    public int RunningCount => _runningJobs.Count;

    internal Task TouchHeartbeatAsync(string jobId, CancellationToken ct = default) =>
        _store.TouchHeartbeatAsync(jobId, ct);

    private void MarkOwnJobsOrphaned()
    {
        try
        {
            _disposed = true;
            foreach (var jobId in _runningJobs.Keys)
            {
                try
                {
                    _store.MarkTerminalAsync(jobId, JobStates.Orphaned, "orphaned", "执行进程退出", partial: false,
                        CancellationToken.None).GetAwaiter().GetResult();
                }
                catch { /* 退出钩子里尽力而为 */ }
            }
        }
        catch { /* 退出钩子绝不抛 */ }
    }

    public ValueTask DisposeAsync()
    {
        MarkOwnJobsOrphaned();
        return ValueTask.CompletedTask;
    }
}

/// <summary>job_start 的返回：未完成（仅 JobId）或已完成（含终态与结果）。</summary>
public sealed class JobStartOutcome
{
    public bool Accepted { get; internal init; }
    public string? JobId { get; internal init; }
    public bool Completed { get; internal init; }
    public string? State { get; internal init; }
    public string? ResultJson { get; internal init; }
    public string? ErrorStatus { get; internal init; }
    public string? ErrorText { get; internal init; }

    public static JobStartOutcome Started(string jobId, string? note = null) =>
        new() { Accepted = true, JobId = jobId, ErrorText = note };
    public static JobStartOutcome Fail(string status, string error) =>
        new() { Accepted = false, ErrorStatus = status, ErrorText = error };
}
