using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Topology;

public partial class TopologyService
{
    private async Task CollectServerIdentityAsync(
        SshServerConfig server,
        DiscoveryResult result,
        List<string> notes,
        CancellationToken ct)
    {
        if (_identityStore is null)
            return;

        var probe = new AssetIdentityProbeService(_driverRegistry);
        var probeResult = await probe.ProbeServerAsync(
            server, (command, token) => ProbeAsync(server, command, token), ct);

        foreach (var note in probeResult.Notes)
            notes.Add($"{server.Name}: 身份{note}");

        await SaveIdentitiesAsync(probeResult, result, notes, ct);
    }

    private async Task CollectDatasourceIdentitiesAsync(
        AppConfig config,
        DiscoveryResult result,
        List<string> notes,
        CancellationToken ct)
    {
        if (_identityStore is null)
            return;

        var probe = new AssetIdentityProbeService(_driverRegistry);
        foreach (var datasource in config.DataSources)
        {
            ct.ThrowIfCancellationRequested();

            var probeResult = await probe.ProbeDatasourceAsync(datasource, ct);
            foreach (var note in probeResult.Notes)
                notes.Add($"{datasource.Name}: 身份{note}");

            await SaveIdentitiesAsync(probeResult, result, notes, ct);
        }
    }

    private async Task SaveIdentitiesAsync(
        AssetIdentityProbeResult probeResult,
        DiscoveryResult result,
        List<string> notes,
        CancellationToken ct)
    {
        if (_identityStore is null)
            return;

        foreach (var identity in probeResult.Identities)
        {
            ct.ThrowIfCancellationRequested();

            if (await _identityStore.TryUpsertAsync(identity))
            {
                result.IdentityCount++;
                continue;
            }

            var conflict = $"{identity.AssetType}:{identity.AssetId} 的 {identity.IdentityKind} 已归属其它资产";
            if (!result.IdentityConflicts.Contains(conflict))
            {
                result.IdentityConflicts.Add(conflict);
                notes.Add("身份冲突: " + conflict);
            }
        }
    }
}
