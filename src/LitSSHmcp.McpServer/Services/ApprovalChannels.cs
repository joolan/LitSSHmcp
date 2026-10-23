using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Approval;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.Logging;

namespace LitSSHmcp.McpServer.Services;

/// <summary>审批模式（<c>security.approval.mode</c>）。</summary>
public enum ApprovalMode
{
    /// <summary>默认：敏感操作需人工确认（桌面弹窗 / 带外 CLI）。</summary>
    Manual,
    /// <summary>危险：敏感操作自动放行，不弹窗。</summary>
    AutoApprove,
    /// <summary>敏感操作直接拒绝。</summary>
    AutoReject
}

public static class ApprovalModeParser
{
    /// <summary>解析审批模式字符串（不区分大小写，容忍常见写法）；未知值按 manual 处理（fail-safe）。</summary>
    public static ApprovalMode Parse(string? mode) => (mode ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "auto-approve" or "auto_approve" or "autoapprove" or "allow" or "approve" => ApprovalMode.AutoApprove,
        "auto-reject" or "auto_reject" or "autoreject" or "deny" or "reject" => ApprovalMode.AutoReject,
        _ => ApprovalMode.Manual
    };
}

/// <summary>一次审批请求的上下文（跨通道共享）。</summary>
public sealed class ApprovalRequestContext
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string ServerName { get; init; }
    public required string Operation { get; init; }
    public required string Command { get; init; }
    public string? FilePath { get; init; }
    public int TimeoutSeconds { get; init; } = 45;
    public bool TopMost { get; init; } = true;
}

public interface IApprovalChannel
{
    string Name { get; }

    /// <summary>
    /// 审批结果。null = 该通道不适用/不可用（交由其它通道决定）；
    /// 具体的 Approved/Rejected/Timeout 一旦返回即为该通道的最终结论。
    /// </summary>
    Task<ApprovalOutcome?> RequestAsync(ApprovalRequestContext context, CancellationToken ct);
}

/// <summary>
/// 可重置的审批超时钟：默认从请求发起时起算；<see cref="Reset"/> 被调用（桌面弹窗变为可见）后重新给满一个窗口。
/// 并行会话下桌面弹窗一次只显示一个（<see cref="DesktopApprovalService"/> 内部闸门），
/// 排队中的请求若沿用发起时刻起算，会在自己的弹窗还没出现前就被判超时（评估报告 §4.2）。
/// </summary>
public sealed class ApprovalTimeoutClock : IDisposable
{
    private readonly TimeSpan _timeout;
    private readonly object _sync = new();
    private CancellationTokenSource _cts = new();

    public ApprovalTimeoutClock(TimeSpan timeout)
    {
        _timeout = timeout;
        Expired = WaitAsync(_timeout, _cts.Token);
    }

    /// <summary>
    /// 当前的超时等待任务：窗口自然届满时**正常完成**；
    /// 被 <see cref="Reset"/>（或 Dispose）取消时以 <see cref="OperationCanceledException"/> 结束——
    /// 调用方据此区分「真超时」与「弹窗可见后重置」。
    /// </summary>
    public Task Expired { get; private set; }

    public void Reset()
    {
        CancellationTokenSource old;
        lock (_sync)
        {
            old = _cts;
            _cts = new CancellationTokenSource();
            Expired = WaitAsync(_timeout, _cts.Token);
        }

        try { old.Cancel(); }
        catch (ObjectDisposedException) { /* ignore */ }
        old.Dispose();
    }

    private static async Task WaitAsync(TimeSpan timeout, CancellationToken ct) =>
        await Task.Delay(timeout, ct).ConfigureAwait(false);

    public void Dispose()
    {
        lock (_sync)
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { /* ignore */ }
            _cts.Dispose();
        }
    }
}

/// <summary>
/// 带外 CLI/IPC 审批通道：写 pending-&lt;id&gt;.json 并轮询 decision-&lt;id&gt;.json。
/// 操作员用 <c>litssh approvals</c> 查看、<c>litssh approve/deny &lt;id&gt;</c> 决定。
/// 适配无桌面/headless 场景。
/// </summary>
public sealed class CliApprovalChannel : IApprovalChannel
{
    private readonly string _directory;
    private readonly ILogger<CliApprovalChannel>? _logger;

    public CliApprovalChannel(ILogger<CliApprovalChannel>? logger = null)
        : this(ConfigPaths.ApprovalsDir, logger)
    {
    }

    public CliApprovalChannel(string directory, ILogger<CliApprovalChannel>? logger = null)
    {
        _directory = directory;
        _logger = logger;
    }

    public string Name => "cli";

    public async Task<ApprovalOutcome?> RequestAsync(ApprovalRequestContext context, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "无法创建带外审批目录，通道弃权: {Dir}", _directory);
            return null;
        }

        var timeout = context.TimeoutSeconds > 0 ? context.TimeoutSeconds : 300;
        var request = new ApprovalRequestFile
        {
            Id = context.Id,
            Server = context.ServerName,
            Operation = context.Operation,
            Command = context.Command.Length > 4000 ? context.Command[..4000] : context.Command,
            FilePath = context.FilePath,
            // 归因随宿主注入（MCP Program 启动时置入 AuditContext；"local"=桌面未登录）——
            // Web 宿主按它过滤推送（报告 §3.3.1），不影响 cli 决策本身
            Owner = AuditContext.CurrentUserId,
            CreatedAt = DateTimeOffset.Now,
            ExpiresAt = DateTimeOffset.Now.AddSeconds(timeout),
            TimeoutSeconds = context.TimeoutSeconds
        };

        ApprovalFileStore.WriteRequest(_directory, request);
        _logger?.LogInformation("带外审批已排队 id={Id}（用 'litssh approvals' 查看, 'litssh approve {Id}' / 'litssh deny {Id}' 决定）", context.Id, context.Id, context.Id);

        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                var decision = ApprovalFileStore.TryReadDecision(_directory, context.Id);
                if (decision != null)
                {
                    _logger?.LogInformation("带外审批结果 id={Id} approved={Approved}", context.Id, decision.Approved);
                    return decision.Approved ? ApprovalOutcome.Approved : ApprovalOutcome.Rejected;
                }

                await Task.Delay(500, ct);
            }

            _logger?.LogWarning("带外审批超时未决 id={Id}", context.Id);
            return ApprovalOutcome.Timeout;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            ApprovalFileStore.Remove(_directory, context.Id);
        }
    }
}

/// <summary>
/// 多通道审批分发器：按 <c>security.approval.channels</c> 启用通道（默认 desktop+cli），
/// 各通道并发等待，**首个给出结论者生效**；全部弃权 → <see cref="ApprovalOutcome.Unavailable"/>；
/// 分发器自身超时 → <see cref="ApprovalOutcome.Timeout"/>（fail-closed，且三种失败可区分）。
/// </summary>
public class ApprovalService : IApprovalService
{
    private readonly ISecurityOptionsProvider _securityOptions;
    private readonly DesktopApprovalService _desktop;
    private readonly CliApprovalChannel _cli;
    private readonly ILogger<ApprovalService>? _logger;
    private readonly ApprovalRoundTracker? _roundTracker;
    private readonly ApprovalExemptionRegistry? _exemptions;

    public ApprovalService(
        ISecurityOptionsProvider securityOptions,
        DesktopApprovalService desktop,
        CliApprovalChannel cli,
        ILogger<ApprovalService>? logger = null,
        ApprovalRoundTracker? roundTracker = null,
        ApprovalExemptionRegistry? exemptions = null)
    {
        _securityOptions = securityOptions;
        _desktop = desktop;
        _cli = cli;
        _logger = logger;
        _roundTracker = roundTracker;
        _exemptions = exemptions;
    }

    /// <summary>
    /// 解析生效的审批通道：环境变量 <c>LITSSH_APPROVAL_CHANNELS</c>（逗号分隔）优先于配置
    /// <c>security.approval.channels</c>（评估报告 §3.2：Web 宿主 spawn 子进程时强制 web/cli、
    /// 关闭 desktop 弹窗通道；子进程经 env 继承，不改共享 config.json）；两者皆空 → desktop+cli。
    /// </summary>
    public static string[] ResolveChannels(string[]? configured)
    {
        var env = Environment.GetEnvironmentVariable("LITSSH_APPROVAL_CHANNELS");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var fromEnv = env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fromEnv.Length > 0)
                return fromEnv;
        }
        return configured is { Length: > 0 } ? configured : new[] { "desktop", "cli" };
    }

    public async Task<ApprovalOutcome> RequestApprovalAsync(
        string serverName,
        string command,
        CommandFilterResult filterResult,
        string? filePath = null,
        string? operationLabel = null,
        CancellationToken ct = default,
        string? riskReport = null)
    {
        var options = _securityOptions.Approval;
        var operation = operationLabel ?? (filterResult == CommandFilterResult.Sensitive ? "敏感命令" : "文件传输");
        // 展示用命令 = 命令 + 风险报告；桌面通道内部会再次 Combine(带 null 幂等)
        var displayCommand = ApprovalDisplay.Combine(command, riskReport);

        // 审批模式：manual(默认) / auto-approve(危险) / auto-reject。
        // 仅影响“需人工确认”的敏感操作；被命令过滤器判为 Blocked 的仍是硬拒绝（在调用本服务前已拦截）。
        switch (ApprovalModeParser.Parse(options.Mode))
        {
            case ApprovalMode.AutoApprove:
                _logger?.LogWarning("审批模式=自动允许(危险)：已自动放行敏感操作 server={Server} op={Operation} cmd={Command}",
                    serverName, operation, command);
                return ApprovalOutcome.Approved;
            case ApprovalMode.AutoReject:
                _logger?.LogInformation("审批模式=自动拒绝：已自动拒绝敏感操作 server={Server} op={Operation}", serverName, operation);
                return ApprovalOutcome.AutoRejected;
        }
        // 预检：用户在弹窗勾选的「本轮次/整个会话」授权命中则直接放行（不再弹窗、不再等通道）。
        var exemptTool = AuditContext.CurrentTool;
        if (_exemptions is not null
            && _exemptions.IsApproved(exemptTool, serverName, command, filePath, _roundTracker?.CurrentRoundId))
        {
            _logger?.LogInformation("敏感操作命中已有授权(工具={Tool}, 目标={Target}), 自动放行", exemptTool, serverName);
            return ApprovalOutcome.Approved;
        }

        // 默认同时开 desktop + cli：无桌面(服务会话/CI/headless)时 desktop 必然失败,
        // 若只配 desktop, 这类环境 100% 走到"拒绝", 是成功率的最大杀手。
        var channels = ResolveChannels(options.Channels);

        var context = new ApprovalRequestContext
        {
            ServerName = serverName,
            Operation = operation,
            Command = displayCommand,
            FilePath = filePath,
            TimeoutSeconds = options.TimeoutSeconds,
            TopMost = options.TopMost
        };

        var timeoutSeconds = options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 300;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var pending = new List<Task<ApprovalOutcome?>>();
        if (channels.Any(c => c.Equals("desktop", StringComparison.OrdinalIgnoreCase)))
            pending.Add(AsChannelTask(_desktop.RequestApprovalAsync(serverName, command, filterResult, filePath, operationLabel, cts.Token, riskReport)));
        if (channels.Any(c => c.Equals("cli", StringComparison.OrdinalIgnoreCase)))
            pending.Add(_cli.RequestAsync(context, cts.Token));

        if (pending.Count == 0)
        {
            _logger?.LogWarning("未配置任何审批通道，按不可用处理");
            return ApprovalOutcome.Unavailable;
        }

        // 超时钟：请求发起即开始计时；桌面弹窗可见后重置为完整窗口
        // （桌面通道内部一次只显示一个弹窗，排队中的请求不重置会在弹窗出现前就被判超时）。
        using var clock = new ApprovalTimeoutClock(TimeSpan.FromSeconds(timeoutSeconds));
        void OnDesktopDialogShown() => clock.Reset();
        _desktop.DialogShown += OnDesktopDialogShown;

        try
        {
            while (pending.Count > 0)
            {
                var current = clock.Expired;
                var finished = await Task.WhenAny(pending.Cast<Task>().Append(current));
                if (ReferenceEquals(finished, current))
                {
                    try
                    {
                        await current;
                    }
                    catch (OperationCanceledException)
                    {
                        // 计时器被“弹窗可见”重置，继续等新的窗口
                        continue;
                    }

                    if (ct.IsCancellationRequested)
                    {
                        _logger?.LogWarning("审批等待被客户端取消");
                        return ApprovalOutcome.Unavailable;
                    }
                    _logger?.LogWarning("审批超时({TimeoutSeconds}s)未决", timeoutSeconds);
                    return ApprovalOutcome.Timeout;
                }

                var channelTask = (Task<ApprovalOutcome?>)finished;
                pending.Remove(channelTask);

                ApprovalOutcome? outcome;
                try { outcome = await channelTask; }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "审批通道异常，视为弃权");
                    outcome = null;
                }

                if (outcome.HasValue)
                {
                    _logger?.LogInformation("审批结论 {Outcome}（{Remaining} 个通道仍在等待）", outcome.Value, pending.Count);
                    return outcome.Value;
                }
            }
        }
        finally
        {
            _desktop.DialogShown -= OnDesktopDialogShown;
            cts.Cancel();
        }

        _logger?.LogWarning("所有审批通道均不可用");
        return ApprovalOutcome.Unavailable;
    }

    private static async Task<ApprovalOutcome?> AsChannelTask(Task<ApprovalOutcome> task) => await task;
}
