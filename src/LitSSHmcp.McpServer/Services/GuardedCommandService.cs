using System.Diagnostics;
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

public sealed class GuardedFleetItem
{
    public string ServerId { get; init; } = string.Empty;
    public string ServerName { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string? Status { get; init; }
    public string? Error { get; init; }
    public string Output { get; init; } = string.Empty;
    public bool Truncated { get; init; }
    public int ExitCode { get; init; }
    public double DurationMs { get; init; }
}

/// <summary>批量执行逐台进度（用于 MCP progress 通知；Message 只放稳定 JSON）。</summary>
public sealed record GuardedFleetProgress(int Completed, int Total, string ServerId, string ServerName,
    string Host, bool Success, string? Error, string Output);

public sealed class GuardedFleetOutcome
{
    public bool Success { get; init; }
    public string? Status { get; init; }
    public string? Error { get; init; }
    public string Command { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public List<GuardedFleetItem> Items { get; init; } = new();
    public int Total { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
}

public interface IGuardedCommandService
{
    /// <summary>
    /// 在指定服务器上执行一条远端命令，统一走：ID 宽松解析 → 命令过滤(Blocked/Sensitive) →
    /// 敏感人工审批 → 执行 → 审计兜底 → 输出截断。
    /// 供 Docker/Service/Log/Java 等领域工具"复用"，避免各自拼装安全链路时漏步骤。
    /// </summary>
    Task<GuardedCommandOutcome> RunAsync(string serverReference, string command, CancellationToken ct = default);

    /// <summary>批量变更：一次审批 + 多台执行；仅支持非脚本命令（脚本文件请走单目标审批）。</summary>
    Task<GuardedFleetOutcome> RunFleetAsync(IReadOnlyList<string> serverReferences, string command,
        int concurrency = 4, int maxTargets = 20, string? operationLabel = null, string? riskReport = null,
        CancellationToken ct = default, IProgress<GuardedFleetProgress>? progress = null, int timeoutSeconds = 60) => Task.FromResult(new GuardedFleetOutcome
        {
            Success = false,
            Status = "not_supported",
            Error = "当前 runner 不支持批量执行",
            Command = command,
            Summary = "当前 runner 不支持批量执行"
        });
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
            await AuditAsync(server, command, CommandStatus.Blocked, null, null, AuditCategory.Gate, "blocked");
            return Blocked(server, "命令被安全策略禁止执行（命中危险命令规则）。");
        }

        if (verdict == CommandFilterResult.Sensitive)
        {
            var outcome = await _approvalService.RequestApprovalAsync(ToolSupport.ServerLabel(server), command, verdict, null, null, ct);
            if (outcome != ApprovalOutcome.Approved)
            {
                await AuditAsync(server, command, CommandStatus.Rejected, null, null, AuditCategory.Gate, ToolSupport.DecisionFor(outcome, _securityOptions.Approval.Mode));
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

            await AuditAsync(server, command, CommandStatus.Approved, null, null, AuditCategory.Gate, ToolSupport.DecisionFor(outcome, _securityOptions.Approval.Mode));
        }

        var result = await _sshService.ExecuteCommandAsync(server, command, ct);

        await AuditAsync(server, command,
            result.Success ? CommandStatus.Executed : CommandStatus.Failed,
            result.Output.Length > 0
                ? (result.Output.Length > ToolSupport.MaxAuditResultChars ? result.Output[..ToolSupport.MaxAuditResultChars] : result.Output)
                : result.Error,
            result.ExitCode, AuditCategory.Exec);

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

    public async Task<GuardedFleetOutcome> RunFleetAsync(IReadOnlyList<string> serverReferences, string command,
        int concurrency = 4, int maxTargets = 20, string? operationLabel = null, string? riskReport = null,
        CancellationToken ct = default, IProgress<GuardedFleetProgress>? progress = null, int timeoutSeconds = 60)
    {
        var references = (serverReferences ?? Array.Empty<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (references.Count == 0)
            return FleetFail("empty_targets", "serverIds 不能为空", command);

        var cap = Math.Clamp(maxTargets, 1, 100);
        if (references.Count > cap)
            return FleetFail("too_many_targets", $"目标数 {references.Count} 超过上限 {cap}", command);

        var config = await _configService.LoadConfigAsync();
        var servers = new List<SshServerConfig>();
        foreach (var reference in references)
        {
            var (server, resolveStatus, resolveError) = ToolSupport.ResolveServer(config, reference);
            if (server is null)
                return FleetFail("server_not_found", $"{reference}: {resolveError ?? resolveStatus}", command);
            servers.Add(server);
        }

        var blocking = ToolSupport.BlockingCommandHint(command);
        if (blocking is not null)
            return FleetFail("blocking_command", blocking, command);

        if (ScriptExecForm.TryParse(command, out _, out _))
            return FleetFail("script_not_allowed",
                "批量变更不支持脚本文件（bash x.sh / python3 x.py / ./x.sh）；请先用单目标审批执行脚本，或改用幂等命令",
                command);

        var verdict = _commandFilter.CheckCommand(command);
        if (verdict == CommandFilterResult.Blocked)
            return FleetFail("blocked", "命令被安全策略禁止执行（命中危险命令规则）", command);

        if (verdict == CommandFilterResult.Sensitive)
        {
            var label = $"批量变更({servers.Count}台)";
            var outcome = await _approvalService.RequestApprovalAsync(label, command, verdict, null,
                operationLabel ?? "批量执行", ct, riskReport);
            if (outcome != ApprovalOutcome.Approved)
            {
                var (approvalStatus, approvalError) = ApprovalOutcomeText.Describe(outcome, _securityOptions.Approval.TimeoutSeconds);
                return FleetFail(approvalStatus, approvalError, command);
            }
        }

        var results = new GuardedFleetItem[servers.Count];
        var completed = 0;
        var parallelism = Math.Clamp(concurrency, 1, 8);
        await Parallel.ForEachAsync(Enumerable.Range(0, servers.Count),
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (index, token) =>
            {
                var server = servers[index];
                var sw = Stopwatch.StartNew();
                try
                {
                    var result = await _sshService.ExecuteCommandAsync(server, command, token, Math.Clamp(timeoutSeconds, 1, 3600));
                    // 批量结果按台截断，避免 100 台输出把模型上下文撑爆；需要完整输出时用单目标工具。
                    var (output, truncated, _) = ToolSupport.Truncate(result.Output,
                        Math.Min(ToolSupport.MaxOutputChars, 600));
                    await AuditAsync(server, command,
                        result.Success ? CommandStatus.Executed : CommandStatus.Failed,
                        result.Output.Length > 0
                            ? (result.Output.Length > ToolSupport.MaxAuditResultChars
                                ? result.Output[..ToolSupport.MaxAuditResultChars]
                                : result.Output)
                            : result.Error,
                        result.ExitCode, AuditCategory.Exec);
                    results[index] = new GuardedFleetItem
                    {
                        ServerId = server.Id,
                        ServerName = server.Name,
                        Host = server.Host,
                        Success = result.Success,
                        Status = result.Success ? null : ToolSupport.CommandFailureStatus(result),
                        Error = result.Success ? null : result.Error,
                        Output = output,
                        Truncated = truncated,
                        ExitCode = result.ExitCode,
                        DurationMs = sw.Elapsed.TotalMilliseconds
                    };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    await AuditAsync(server, command, CommandStatus.Failed, ex.Message, null, AuditCategory.Exec);
                    results[index] = new GuardedFleetItem
                    {
                        ServerId = server.Id,
                        ServerName = server.Name,
                        Host = server.Host,
                        Success = false,
                        Status = "exception",
                        Error = ex.Message,
                        Output = string.Empty,
                        DurationMs = sw.Elapsed.TotalMilliseconds
                    };
                }

                var item = results[index];
                if (item is not null)
                {
                    try
                    {
                        progress?.Report(new GuardedFleetProgress(
                            Interlocked.Increment(ref completed), servers.Count,
                            item.ServerId, item.ServerName, item.Host, item.Success, item.Error, item.Output));
                    }
                    catch
                    {
                        // 进度通知失败（对端断开）不影响批量执行结果
                    }
                }
            });

        var succeeded = results.Count(r => r.Success);
        var failed = results.Length - succeeded;
        var status = failed == 0 ? null : succeeded == 0 ? "failed" : "partial_failure";
        return new GuardedFleetOutcome
        {
            Success = failed == 0,
            Status = status,
            Error = failed == 0 ? null : $"{failed}/{results.Length} 台失败",
            Command = command,
            Summary = $"批量执行 {servers.Count} 台: 成功 {succeeded}, 失败 {failed}",
            Items = results.ToList(),
            Total = results.Length,
            Succeeded = succeeded,
            Failed = failed
        };
    }

    private static GuardedFleetOutcome FleetFail(string status, string error, string command) => new()
    {
        Success = false,
        Status = status,
        Error = error,
        Command = command,
        Summary = error
    };

    private static GuardedCommandOutcome Blocked(SshServerConfig server, string error) => new()
    {
        ServerFound = true,
        ServerId = server.Id,
        ServerName = server.Name,
        ServerHost = server.Host,
        Status = "blocked",
        Error = error
    };

    private Task AuditAsync(SshServerConfig server, string command, CommandStatus status, string? result, int? exitCode, AuditCategory category, string? decision = null) =>
        ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Command = command,
            Result = result,
            Status = status,
            ExitCode = exitCode,
            Category = category,
            Decision = decision
        });
}
