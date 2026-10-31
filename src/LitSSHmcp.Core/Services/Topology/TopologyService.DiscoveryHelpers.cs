using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.Core.Services.Topology;

public partial class TopologyService
{
    public static string BuildConfigScanCommand(IEnumerable<string> paths) =>
        "grep -rE --include='application*.yml' --include='application*.yaml' --include='application*.properties' " +
        "--include='bootstrap*.yml' --include='bootstrap*.properties' --include='logback*.xml' --include='*.conf' " +
        "'jdbc:mysql://|jdbc:postgresql://|redis://|amqp://|amqps://|kafka://|bootstrap[.-]servers|proxy_pass|upstream' " +
        $"{ShellQuote.Join(paths)} 2>/dev/null | head -120";

    private async Task<CommandResult> ProbeAsync(
        SshServerConfig server, string command, CancellationToken ct, bool useSudo = false)
    {
        if (_commandFilter.CheckCommand(command) == CommandFilterResult.Blocked)
        {
            await AuditAsync(server, command, CommandStatus.Blocked, null, null);
            return new CommandResult
            {
                Success = false,
                Error = "blocked_command: 命令被安全策略禁止执行",
                ExitCode = -1
            };
        }

        var result = useSudo && server.SudoType != SudoType.None
            ? await _sshService.ExecuteWithSudoAsync(server, command, ct)
            : await _sshService.ExecuteCommandAsync(server, command, ct);

        await AuditAsync(server, command,
            result.Success ? CommandStatus.Executed : CommandStatus.Failed,
            result.Output, result.ExitCode);
        return result;
    }

    private async Task AuditAsync(
        SshServerConfig server, string command, CommandStatus status, string? output, int? exitCode)
    {
        var log = new CommandAuditLog
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Command = command,
            Result = output == null ? null : Truncate(output, 4000),
            Status = status,
            ExitCode = exitCode
        };

        try
        {
            if (!_auditReady)
            {
                await _auditLogService.InitializeAsync();
                _auditReady = true;
            }
            await _auditLogService.LogCommandAsync(log);
            return;
        }
        catch
        {
            _auditReady = false;
        }

        try { await _auditLogService.LogCommandAsync(log); }
        catch { }
    }

    private static TopologyEdge NewEdge(string from, string to, string type, string evidence) => new()
    {
        From = from,
        To = to,
        Type = type,
        Evidence = evidence,
        Source = "discovered",
        FirstSeenAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow,
        Confidence = 0.8,
        ValidFrom = DateTime.UtcNow,
        LastVerifiedAt = DateTime.UtcNow
    };
}
