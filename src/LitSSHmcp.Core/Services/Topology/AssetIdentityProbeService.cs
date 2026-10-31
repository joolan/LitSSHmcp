using System.Text.RegularExpressions;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.Core.Services.Topology;

public sealed class AssetIdentityProbeResult
{
    public string AssetId { get; init; } = string.Empty;
    public string AssetType { get; init; } = string.Empty;
    public List<AssetIdentityRecord> Identities { get; } = new();
    public List<string> Notes { get; } = new();
}

/// <summary>只读身份探测：SSH 机器身份、MySQL/PostgreSQL/Redis 实例标识。失败不抛异常，只返回 notes。</summary>
public sealed class AssetIdentityProbeService
{
    private readonly IDatasourceDriverRegistry _driverRegistry;

    public AssetIdentityProbeService(IDatasourceDriverRegistry driverRegistry)
    {
        _driverRegistry = driverRegistry;
    }

    public async Task<AssetIdentityProbeResult> ProbeServerAsync(
        SshServerConfig server,
        Func<string, CancellationToken, Task<CommandResult>> runCommand,
        CancellationToken ct = default)
    {
        var result = new AssetIdentityProbeResult
        {
            AssetId = ResolveAssetId(server.AssetKey, server.Id),
            AssetType = "ssh"
        };

        var machineId = await ProbeCommandAsync(
            "machine-id",
            "cat /etc/machine-id 2>/dev/null || cat /var/lib/dbus/machine-id 2>/dev/null",
            runCommand, result, ct);
        Add(result, AssetIdentityKinds.SshMachineId, FirstLine(machineId), "probe:ssh", 0.98);

        var hostname = await ProbeCommandAsync(
            "hostname",
            "hostnamectl --static 2>/dev/null || hostname 2>/dev/null",
            runCommand, result, ct);
        Add(result, AssetIdentityKinds.SshHostname, NormalizeHostname(FirstLine(hostname)), "probe:ssh", 0.5);

        var hostKey = await ProbeCommandAsync(
            "host-key",
            "ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub 2>/dev/null || " +
            "ssh-keygen -lf /etc/ssh/ssh_host_rsa_key.pub 2>/dev/null",
            runCommand, result, ct);
        Add(result, AssetIdentityKinds.SshHostKey, ParseHostKeyFingerprint(hostKey), "probe:ssh", 0.95);

        return result;
    }

    public async Task<AssetIdentityProbeResult> ProbeDatasourceAsync(
        DataSourceConfig datasource, CancellationToken ct = default)
    {
        var result = new AssetIdentityProbeResult
        {
            AssetId = ResolveAssetId(datasource.AssetKey, datasource.InstanceKey, datasource.Id),
            AssetType = "datasource"
        };

        var driver = _driverRegistry.Get(datasource.Type);
        if (driver is null)
            return result;

        switch ((datasource.Type ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "mysql":
            {
                var value = await ProbeQueryAsync(
                    datasource, driver, "SELECT @@server_uuid", result, ct);
                Add(result, AssetIdentityKinds.MysqlServerUuid, value, "probe:mysql", 0.98);
                break;
            }
            case "postgres":
            case "postgresql":
            {
                var value = await ProbeQueryAsync(
                    datasource, driver, "SELECT system_identifier FROM pg_control_system()", result, ct);
                Add(result, AssetIdentityKinds.PostgresSystemIdentifier, value, "probe:postgres", 0.98);
                break;
            }
            case "redis":
            {
                var value = await ProbeQueryAsync(
                    datasource, driver, "CLUSTER MYID", result, ct);
                Add(result, AssetIdentityKinds.RedisClusterId, value, "probe:redis", 0.8);
                break;
            }
        }

        return result;
    }

    private static async Task<string?> ProbeCommandAsync(
        string label,
        string command,
        Func<string, CancellationToken, Task<CommandResult>> runCommand,
        AssetIdentityProbeResult result,
        CancellationToken ct)
    {
        try
        {
            var commandResult = await runCommand(command, ct);
            if (commandResult.Success)
                return commandResult.Output;

            if (!string.IsNullOrWhiteSpace(commandResult.Error))
                result.Notes.Add($"{label} 探测失败: {Truncate(commandResult.Error, 160)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Notes.Add($"{label} 探测失败: {Truncate(ex.Message, 160)}");
        }

        return null;
    }

    private static async Task<string?> ProbeQueryAsync(
        DataSourceConfig datasource,
        IDatasourceDriver driver,
        string sql,
        AssetIdentityProbeResult result,
        CancellationToken ct)
    {
        try
        {
            var query = await driver.QueryAsync(datasource, sql, 1, ct);
            if (!query.Success)
            {
                if (!string.IsNullOrWhiteSpace(query.Error))
                    result.Notes.Add($"{datasource.Name}: 身份探测失败: {Truncate(query.Error, 160)}");
                return null;
            }

            var row = query.Rows.FirstOrDefault();
            return row?.Values.FirstOrDefault()?.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Notes.Add($"{datasource.Name}: 身份探测失败: {Truncate(ex.Message, 160)}");
            return null;
        }
    }

    private static void Add(
        AssetIdentityProbeResult result,
        string kind,
        string? value,
        string source,
        double confidence)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return;

        result.Identities.Add(new AssetIdentityRecord
        {
            AssetId = result.AssetId,
            AssetType = result.AssetType,
            IdentityKind = kind,
            IdentityValue = value,
            Source = source,
            Confidence = confidence,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow
        });
    }

    private static string ResolveAssetId(params string?[] candidates) =>
        candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private static string? FirstLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.Length > 0);
    }

    private static string? NormalizeHostname(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('.').ToLowerInvariant();

    private static string? ParseHostKeyFingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = Regex.Match(value, @"SHA256:[A-Za-z0-9+/=]+");
        return match.Success ? match.Value : null;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
