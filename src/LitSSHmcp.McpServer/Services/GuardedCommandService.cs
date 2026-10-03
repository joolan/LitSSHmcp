using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.McpServer.Services;

/// <summary>受保护命令的执行结果（安全过滤 + 审批 + 审计 + 截断 之后）。</summary>
public sealed class GuardedCommandOutcome
{
    /// <summary>false 表示服务器标识没解析到（<see cref="Error"/> 已含可用 ID 提示）。</summary>
    public bool ServerFound { get; init; }

    public string? ServerId { get; init; }
    public string? ServerName { get; init; }
    public string? ServerHost { get; init; }

    public bool Success { get; init; }
    public string? Status { get; init; }
    public string? Error { get; init; }
    public string Output { get; init; } = string.Empty;
    public bool Truncated { get; init; }
    public long OutputChars { get; init; }
    public int ExitCode { get; init; }
    public double DurationMs { get; init; }
}

public interface IGuardedCommandService
{
    /// <summary>
    /// 在指定服务器上执行一条远端命令，统一走：ID 宽松解析 → 命令过滤(Blocked/Sensitive) →
    /// 敏感人工审批 → 执行 → 审计兜底 → 输出截断。
    /// 供 Docker/Service/Log/Java 等"领域工具"复用，避免各自拼装安全链路时漏步骤。
    /// </summary>
    Task<GuardedCommandOutcome> RunAsync(string serverReference, string command, CancellationToken ct = default);
}

public sealed class GuardedCommandService : IGuardedCommandService
{
    private readonly IConfigService _configService;
    private readonly ICommandFilterService _commandFilter;
    private readonly IApprovalService _approvalService;
    private readonly IAuditLogService _auditLogService;
    private readonly ISecurityOptionsProvider _securityOptions;
    private readonly ISshService _sshService;

    public GuardedCommandService(
        IConfigService configService,
        ICommandFilterService commandFilter,
        IApprovalService approvalService,
        IAuditLogService auditLogService,
        ISecurityOptionsProvider securityOptions,
        ISshService sshService)
    {
        _configService = configService;
        _commandFilter = commandFilter;
        _approvalService = approvalService;
        _auditLogService = auditLogService;
        _securityOptions = securityOptions;
        _sshService = sshService;
    }

    public async Task<GuardedCommandOutcome> RunAsync(string serverReference, string command, CancellationToken ct = default)
    {
        var config = await _configService.LoadConfigAsync();
        var (server, resolveStatus, resolveError) = ToolSupport.ResolveServer(config, serverReference);
        if (server == null)
            return new GuardedCommandOutcome { ServerFound = false, Status = resolveStatus, Error = resolveError };

        var verdict = _commandFilter.CheckCommand(command);

        if (verdict == CommandFilterResult.Blocked)
        {
            await AuditAsync(server, command, CommandStatus.Blocked, null, null);
            return Blocked(server, "命令被安全策略禁止执行（命中危险命令规则）。");
        }

        if (verdict == CommandFilterResult.Sensitive)
        {
            var outcome = await _approvalService.RequestApprovalAsync(ToolSupport.ServerLabel(server), command, verdict, null, null, ct);
            if (outcome != ApprovalOutcome.Approved)
            {
                await AuditAsync(server, command, CommandStatus.Rejected, null, null);
                var (status, error) = ApprovalOutcomeText.Describe(outcome, _securityOptions.Approval.TimeoutSeconds);
                return new GuardedCommandOutcome
                {
                    ServerFound = true,
                    ServerId = server.Id,
                    ServerName = server.Name,
                    ServerHost = server.Host,
                    Status = status,
                    Error = error
                };
            }

            await AuditAsync(server, command, CommandStatus.Approved, null, null);
        }

        var result = await _sshService.ExecuteCommandAsync(server, command, ct);

        await AuditAsync(server, command,
            result.Success ? CommandStatus.Executed : CommandStatus.Failed,
            result.Output.Length > 0
                ? (result.Output.Length > ToolSupport.MaxAuditResultChars ? result.Output[..ToolSupport.MaxAuditResultChars] : result.Output)
                : result.Error,
            result.ExitCode);

        var (output, truncated, originalLength) = ToolSupport.Truncate(result.Output, ToolSupport.MaxOutputChars);
        return new GuardedCommandOutcome
        {
            ServerFound = true,
            ServerId = server.Id,
            ServerName = server.Name,
            ServerHost = server.Host,
            Success = result.Success,
            Status = result.Success ? null : ToolSupport.CommandFailureStatus(result),
            Error = result.Success ? null : result.Error,
            Output = output,
            Truncated = truncated,
            OutputChars = originalLength,
            ExitCode = result.ExitCode,
            DurationMs = result.Duration.TotalMilliseconds
        };
    }

    private static GuardedCommandOutcome Blocked(SshServerConfig server, string error) => new()
    {
        ServerFound = true,
        ServerId = server.Id,
        ServerName = server.Name,
        ServerHost = server.Host,
        Status = "blocked",
        Error = error
    };

    private Task AuditAsync(SshServerConfig server, string command, CommandStatus status, string? result, int? exitCode) =>
        ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Command = command,
            Result = result,
            Status = status,
            ExitCode = exitCode
        });
}
