using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 保存后的后台只读身份探测：不阻塞保存、不弹窗，失败静默。
/// 发现流程仍走 TopologyService 的过滤/审计路径；这里仅用于让新保存的配置尽快进入身份库。
/// </summary>
public static class AssetIdentitySaveProbe
{
    public static async Task CaptureServerAsync(
        IConfigService configService,
        ISshService sshService,
        SshServerConfig server)
    {
        try
        {
            if (server.Disabled)
                return;

            var config = await configService.LoadConfigAsync();
            if (!(config.Security?.Discovery?.IdentityProbeEnabled ?? true))
                return;

            var security = new SecurityOptionsProvider();
            var filter = new CommandFilterService(security);
            var audit = new AuditLogService(security);
            try { await audit.InitializeAsync(); } catch { }

            var probe = new AssetIdentityProbeService(
                new DatasourceDriverRegistry(Array.Empty<IDatasourceDriver>()));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await probe.ProbeServerAsync(server, async (command, token) =>
            {
                if (filter.CheckCommand(command) == CommandFilterResult.Blocked)
                {
                    await AuditAsync(audit, server, command, CommandStatus.Blocked, "blocked_command", -1);
                    return new CommandResult
                    {
                        Success = false,
                        Error = "blocked_command",
                        ExitCode = -1
                    };
                }

                var commandResult = await sshService.ExecuteCommandAsync(server, command, token);
                await AuditAsync(audit, server, command,
                    commandResult.Success ? CommandStatus.Executed : CommandStatus.Failed,
                    commandResult.Output, commandResult.ExitCode);
                return commandResult;
            }, cts.Token);

            await SaveAsync(result.Identities);
        }
        catch
        {
            // 后台探测失败不影响保存
        }
    }

    public static async Task CaptureDatasourceAsync(
        IConfigService configService,
        DataSourceConfig datasource)
    {
        try
        {
            var config = await configService.LoadConfigAsync();
            if (!(config.Security?.Discovery?.IdentityProbeEnabled ?? true))
                return;

            var probe = new AssetIdentityProbeService(AppServiceFactory.CreateDriverRegistry(configService));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await probe.ProbeDatasourceAsync(datasource, cts.Token);
            await SaveAsync(result.Identities);
        }
        catch
        {
            // 后台探测失败不影响保存
        }
    }

    private static async Task SaveAsync(IEnumerable<AssetIdentityRecord> identities)
    {
        var store = new AssetIdentityStore();
        await store.InitializeAsync();
        foreach (var identity in identities)
        {
            try { await store.TryUpsertAsync(identity); } catch { }
        }
    }

    private static async Task AuditAsync(
        IAuditLogService audit,
        SshServerConfig server,
        string command,
        CommandStatus status,
        string? result,
        int? exitCode)
    {
        try
        {
            await audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = server.Id,
                ServerName = server.Name,
                Command = command,
                Result = result,
                Status = status,
                ExitCode = exitCode
            });
        }
        catch
        {
            // 审计失败不影响探测
        }
    }
}
