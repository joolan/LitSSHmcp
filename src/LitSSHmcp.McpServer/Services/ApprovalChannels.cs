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

    public ApprovalService(
        ISecurityOptionsProvider securityOptions,
        DesktopApprovalService desktop,
        CliApprovalChannel cli,
        ILogger<ApprovalService>? logger = null)
    {
        _securityOptions = securityOptions;
        _desktop = desktop;
        _cli = cli;
        _logger = logger;
    }

    public async Task<ApprovalOutcome> RequestApprovalAsync(
        string serverName,
        string command,
        CommandFilterResult filterResult,
        string? filePath = null,
        string? operationLabel = null,
        CancellationToken ct = default)
    {
        var options = _securityOptions.Approval;
        var operation = operationLabel ?? (filterResult == CommandFilterResult.Sensitive ? "敏感命令" : "文件传输");

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
        // 默认同时开 desktop + cli：无桌面(服务会话/CI/headless)时 desktop 必然失败,
        // 若只配 desktop, 这类环境 100% 走到"拒绝", 是成功率的最大杀手。
        var channels = options.Channels is { Length: > 0 } ? options.Channels : new[] { "desktop", "cli" };

        var context = new ApprovalRequestContext
        {
            ServerName = serverName,
            Operation = operation,
            Command = command,
            FilePath = filePath,
            TimeoutSeconds = options.TimeoutSeconds,
            TopMost = options.TopMost
        };

        var timeoutSeconds = options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 300;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var pending = new List<Task<ApprovalOutcome?>>();
        if (channels.Any(c => c.Equals("desktop", StringComparison.OrdinalIgnoreCase)))
            pending.Add(AsChannelTask(_desktop.RequestApprovalAsync(serverName, command, filterResult, filePath, operationLabel, cts.Token)));
        if (channels.Any(c => c.Equals("cli", StringComparison.OrdinalIgnoreCase)))
            pending.Add(_cli.RequestAsync(context, cts.Token));

        if (pending.Count == 0)
        {
            _logger?.LogWarning("未配置任何审批通道，按不可用处理");
            return ApprovalOutcome.Unavailable;
        }

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var delay = DelayAsync(TimeSpan.FromSeconds(timeoutSeconds), delayCts.Token);

        try
        {
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending.Cast<Task>().Append(delay));
                if (ReferenceEquals(finished, delay))
                {
                    await delay;
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
            cts.Cancel();
        }

        _logger?.LogWarning("所有审批通道均不可用");
        return ApprovalOutcome.Unavailable;
    }

    private static async Task<ApprovalOutcome?> AsChannelTask(Task<ApprovalOutcome> task) => await task;

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { /* 由 finally 的 Cancel 触发，不影响结论 */ }
    }
}
