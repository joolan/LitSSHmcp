using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Approval;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Cli;

/// <summary>
/// CLI 与桌面/MCP 共用同一套安全配置、SSH 主机密钥、命令过滤、带外审批与审计。
/// </summary>
internal static class CliSecurity
{
    public static SecurityOptionsProvider CreateSecurity() => new();

    public static ISshService CreateSshService(SecurityOptionsProvider security)
    {
        var knownHosts = new FileSshKnownHostsStore();
        var limiter = new TargetLimiter(security, ConfigPaths.TargetStateDir);
        var pool = new SshConnectionPool(
            knownHosts,
            () => security.SshHostKey.Mode,
            () => security.ConnectionPool);
        return new SshService(knownHosts, security, limiter, pool);
    }

    public static async Task<AuditLogService> CreateAuditAsync()
    {
        var audit = new AuditLogService(new SecurityOptionsProvider());
        await audit.InitializeAsync();
        audit.SessionId = "cli-" + Guid.NewGuid().ToString("N")[..8];
        return audit;
    }

    public static async Task<bool> RequireApprovalAsync(
        SecurityOptionsProvider security,
        AuditLogService audit,
        SshServerConfig server,
        string operation,
        string command)
    {
        var mode = security.Approval.Mode;
        if (string.Equals(mode, "auto-approve", StringComparison.OrdinalIgnoreCase))
        {
            await AuditGateAsync(audit, server, command, CommandStatus.Approved, "auto-approve");
            return true;
        }

        if (string.Equals(mode, "auto-reject", StringComparison.OrdinalIgnoreCase))
        {
            await AuditGateAsync(audit, server, command, CommandStatus.Rejected, "auto-reject");
            Console.WriteLine("安全策略：审批模式为 auto-reject，已拒绝执行。");
            return false;
        }

        var channels = security.Approval.Channels ?? Array.Empty<string>();
        if (!channels.Any(channel => string.Equals(channel, "cli", StringComparison.OrdinalIgnoreCase)))
        {
            await AuditGateAsync(audit, server, command, CommandStatus.Rejected, "unavailable");
            Console.WriteLine("安全策略：当前审批通道未启用 CLI（security.approval.channels）。");
            return false;
        }

        var timeoutSeconds = security.Approval.TimeoutSeconds;
        if (timeoutSeconds <= 0)
            timeoutSeconds = 300;
        timeoutSeconds = Math.Clamp(timeoutSeconds, 5, 300);

        var id = Guid.NewGuid().ToString("N")[..12];
        var dir = ConfigPaths.ApprovalsDir;
        var request = new ApprovalRequestFile
        {
            Id = id,
            Server = string.IsNullOrWhiteSpace(server.Name) ? server.Id : server.Name,
            Operation = operation,
            Command = SecretRedactor.Redact(command),
            Owner = Environment.GetEnvironmentVariable("LITSSH_OWNER_USER_ID") ?? "local",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds),
            TimeoutSeconds = timeoutSeconds
        };

        try
        {
            ApprovalFileStore.WriteRequest(dir, request);
        }
        catch (Exception ex)
        {
            await AuditGateAsync(audit, server, command, CommandStatus.Rejected, "unavailable");
            Console.WriteLine("无法写入待审批请求：" + ex.Message);
            return false;
        }

        Console.WriteLine();
        Console.WriteLine($"需要审批：{operation}");
        Console.WriteLine($"  服务器：{request.Server}");
        Console.WriteLine($"  命令：{SecretRedactor.Redact(command)}");
        Console.WriteLine($"  审批 ID：{id}");
        Console.WriteLine($"  批准：litssh approve {id}");
        Console.WriteLine($"  拒绝：litssh deny {id}");
        Console.WriteLine($"  超时：{timeoutSeconds} 秒");
        Console.WriteLine();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var decision = ApprovalFileStore.TryReadDecision(dir, id);
            if (decision is not null)
            {
                ApprovalFileStore.Remove(dir, id);
                if (decision.Approved)
                {
                    await AuditGateAsync(audit, server, command, CommandStatus.Approved,
                        decision.Channel ?? "cli");
                    return true;
                }

                await AuditGateAsync(audit, server, command, CommandStatus.Rejected,
                    decision.Channel ?? "cli");
                Console.WriteLine("审批被拒绝，已取消执行。");
                return false;
            }

            await Task.Delay(500);
        }

        ApprovalFileStore.Remove(dir, id);
        await AuditGateAsync(audit, server, command, CommandStatus.Rejected, "timeout");
        Console.WriteLine("审批超时，已取消执行。");
        return false;
    }

    public static Task AuditGateAsync(AuditLogService audit, SshServerConfig server, string command,
        CommandStatus status, string decision)
        => audit.LogCommandAsync(new CommandAuditLog
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Command = SecretRedactor.Redact(command),
            Status = status,
            Category = AuditCategory.Gate,
            Decision = decision,
            Tool = "cli",
            UserId = "local"
        });

    public static Task AuditExecutionAsync(AuditLogService audit, SshServerConfig server, string command,
        CommandStatus status, string? result)
        => audit.LogCommandAsync(new CommandAuditLog
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Command = SecretRedactor.Redact(command),
            Result = result,
            Status = status,
            Category = AuditCategory.Exec,
            Tool = "cli",
            UserId = "local"
        });
}
