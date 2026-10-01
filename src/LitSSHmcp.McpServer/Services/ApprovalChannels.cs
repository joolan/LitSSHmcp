using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Approval;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.Logging;

namespace LitSSHmcp.McpServer.Services;

/// <summary>一次审批请求的上下文（跨通道共享）。</summary>
public sealed class ApprovalRequestContext
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string ServerName { get; init; }
    public required string Operation { get; init; }
    public required string Command { get; init; }
    public string? FilePath { get; init; }
    public int TimeoutSeconds { get; init; } = 120;
    public bool TopMost { get; init; } = true;
}

public interface IApprovalChannel
{
    string Name { get; }

    /// <summary>true=批准；false=拒绝；null=该通道不适用/不可用（交由其它通道决定）。</summary>
    Task<bool?> RequestAsync(ApprovalRequestContext context, CancellationToken ct);
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

    public async Task<bool?> RequestAsync(ApprovalRequestContext context, CancellationToken ct)
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
                    return decision.Approved;
                }

                await Task.Delay(500, ct);
            }

            _logger?.LogWarning("带外审批超时未决，按拒绝处理 id={Id}", context.Id);
            return false;
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
/// 多通道审批分发器：按 <c>security.approval.channels</c> 启用通道（默认 desktop），
/// 各通道并发等待，**首个给出决定者生效**；全部弃权或超时 → 拒绝（fail-closed）。
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

    public async Task<bool> RequestApprovalAsync(
        string serverName,
        string command,
        CommandFilterResult filterResult,
        string? filePath = null,
        string? operationLabel = null)
    {
        var options = _securityOptions.Approval;
        var operation = operationLabel ?? (filterResult == CommandFilterResult.Sensitive ? "敏感命令" : "文件传输");
        var channels = options.Channels is { Length: > 0 } ? options.Channels : new[] { "desktop" };

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
        using var cts = new CancellationTokenSource();

        var pending = new List<Task<bool?>>();
        if (channels.Any(c => c.Equals("desktop", StringComparison.OrdinalIgnoreCase)))
            pending.Add(DesktopAsync(serverName, command, filterResult, filePath, operationLabel));
        if (channels.Any(c => c.Equals("cli", StringComparison.OrdinalIgnoreCase)))
            pending.Add(_cli.RequestAsync(context, cts.Token));

        if (pending.Count == 0)
        {
            _logger?.LogWarning("未配置任何审批通道，按拒绝处理");
            return false;
        }

        var delay = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending.Cast<Task>().Append(delay));
                if (ReferenceEquals(finished, delay))
                {
                    _logger?.LogWarning("审批超时({TimeoutSeconds}s)未决，按拒绝处理", timeoutSeconds);
                    return false;
                }

                var channelTask = (Task<bool?>)finished;
                pending.Remove(channelTask);

                bool? decision;
                try { decision = await channelTask; }
                catch { decision = null; }

                if (decision is bool approved)
                {
                    _logger?.LogInformation("审批决定 approved={Approved}（{Remaining} 个通道仍在等待）", approved, pending.Count);
                    return approved;
                }
            }
        }
        finally
        {
            cts.Cancel();
        }

        _logger?.LogWarning("所有审批通道均不可用，按拒绝处理");
        return false;
    }

    private async Task<bool?> DesktopAsync(
        string serverName,
        string command,
        CommandFilterResult filterResult,
        string? filePath,
        string? operationLabel)
        => await _desktop.RequestApprovalAsync(serverName, command, filterResult, filePath, operationLabel);
}
