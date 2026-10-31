using System.Text.RegularExpressions;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.Topology;

public interface IAssetConfigurationSuggestionService
{
    Task<List<AssetConfigurationSuggestion>> GetSuggestionsAsync(CancellationToken ct = default);
    Task<bool> ApplyAsync(AssetConfigurationSuggestion suggestion, CancellationToken ct = default);
}

/// <summary>
/// 从自动发现证据和现有配置生成“可补全配置”建议；只读扫描，应用建议时才写配置。
/// </summary>
public sealed class AssetConfigurationSuggestionService : IAssetConfigurationSuggestionService
{
    private readonly IConfigService _configService;
    private readonly ITopologyService _topologyService;

    public AssetConfigurationSuggestionService(
        IConfigService configService, ITopologyService topologyService)
    {
        _configService = configService;
        _topologyService = topologyService;
    }

    public async Task<List<AssetConfigurationSuggestion>> GetSuggestionsAsync(CancellationToken ct = default)
    {
        var config = await _configService.LoadConfigAsync();
        var servers = config.Servers ?? Array.Empty<SshServerConfig>();
        var evidenceByServer = new Dictionary<string, ServerDiscoveryEvidence?>(StringComparer.OrdinalIgnoreCase);

        foreach (var server in servers)
        {
            ct.ThrowIfCancellationRequested();
            evidenceByServer[server.Id] = await _topologyService.GetServerDiscoveryEvidenceAsync(server.Id, ct);
        }

        var suggestions = new List<AssetConfigurationSuggestion>();
        var serverKeys = servers
            .Select(s => NormalizeKey(s.AssetKey))
            .Where(k => k is not null)
            .Select(k => k!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var appKeys = (config.Applications ?? Array.Empty<ApplicationConfig>())
            .Select(a => NormalizeKey(a.AssetKey))
            .Where(k => k is not null)
            .Select(k => k!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dsKeys = (config.DataSources ?? Array.Empty<DataSourceConfig>())
            .Select(d => NormalizeKey(d.AssetKey))
            .Where(k => k is not null)
            .Select(k => k!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var server in servers)
        {
            if (string.IsNullOrWhiteSpace(server.AssetKey))
            {
                suggestions.Add(new AssetConfigurationSuggestion
                {
                    AssetId = server.Id,
                    AssetType = "ssh",
                    AssetName = server.Name,
                    Kind = AssetSuggestionKind.AssetKey,
                    Value = MakeUniqueKey(server.Name, server.Id, serverKeys),
                    Reason = "服务器没有 AssetKey，无法被运行环境引用",
                    Confidence = 0.95
                });
            }
        }

        foreach (var app in config.Applications ?? Array.Empty<ApplicationConfig>())
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(app.AssetKey))
            {
                suggestions.Add(new AssetConfigurationSuggestion
                {
                    AssetId = app.Id,
                    AssetType = "application",
                    AssetName = app.Name,
                    Kind = AssetSuggestionKind.AssetKey,
                    Value = MakeUniqueKey(app.Name, app.Id, appKeys),
                    Reason = "应用没有 AssetKey，无法被运行环境/URL 稳定引用",
                    Confidence = 0.9
                });
            }

            var server = FindServerForApplication(app, servers, evidenceByServer);
            if (app.RuntimeKind == RuntimeEnvironmentKind.Unspecified && server is not null &&
                !string.IsNullOrWhiteSpace(server.AssetKey))
            {
                suggestions.Add(new AssetConfigurationSuggestion
                {
                    AssetId = app.Id,
                    AssetType = "application",
                    AssetName = app.Name,
                    Kind = AssetSuggestionKind.RuntimeSsh,
                    Value = server.AssetKey!,
                    Reason = $"发现应用可能运行在服务器 {server.Name}",
                    Confidence = 0.85
                });
            }

            if (IsNginx(app) && server is not null && evidenceByServer.TryGetValue(server.Id, out var evidence) &&
                evidence is not null)
            {
                foreach (var domain in evidence.Domains.Where(d => !d.Domain.StartsWith("*.", StringComparison.Ordinal)))
                {
                    var url = $"https://{domain.Domain}/";
                    if (app.Urls?.Any(u => string.Equals(
                            u.Url.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) == true)
                        continue;

                    suggestions.Add(new AssetConfigurationSuggestion
                    {
                        AssetId = app.Id,
                        AssetType = "application",
                        AssetName = app.Name,
                        Kind = AssetSuggestionKind.Url,
                        Value = url,
                        Role = UrlRole.PublicEntry,
                        Reason = $"发现 Nginx 站点 {domain.Domain}，建议作为入口 URL",
                        Confidence = 0.8
                    });
                }
            }
        }

        foreach (var datasource in config.DataSources ?? Array.Empty<DataSourceConfig>())
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(datasource.AssetKey))
            {
                suggestions.Add(new AssetConfigurationSuggestion
                {
                    AssetId = datasource.Id,
                    AssetType = "datasource",
                    AssetName = datasource.Name,
                    Kind = AssetSuggestionKind.AssetKey,
                    Value = MakeUniqueKey(datasource.Name, datasource.Id, dsKeys),
                    Reason = "数据源没有 AssetKey，无法被运行环境稳定引用",
                    Confidence = 0.9
                });
            }

            if (datasource.RuntimeKind != RuntimeEnvironmentKind.Unspecified)
                continue;

            var server = FindServerForDatasource(datasource, servers, evidenceByServer);
            if (server is not null && !string.IsNullOrWhiteSpace(server.AssetKey))
            {
                suggestions.Add(new AssetConfigurationSuggestion
                {
                    AssetId = datasource.Id,
                    AssetType = "datasource",
                    AssetName = datasource.Name,
                    Kind = AssetSuggestionKind.RuntimeSsh,
                    Value = server.AssetKey!,
                    Reason = $"发现数据源可能运行在服务器 {server.Name}",
                    Confidence = 0.8
                });
            }
        }

        return suggestions
            .OrderByDescending(s => s.Confidence)
            .ThenBy(s => s.AssetType, StringComparer.Ordinal)
            .ThenBy(s => s.AssetName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Kind)
            .ToList();
    }

    public async Task<bool> ApplyAsync(
        AssetConfigurationSuggestion suggestion, CancellationToken ct = default)
    {
        var config = await _configService.LoadConfigAsync();

        switch (suggestion.AssetType)
        {
            case "ssh":
            {
                var server = config.Servers.FirstOrDefault(s => s.Id == suggestion.AssetId);
                if (server is null)
                    return false;
                if (suggestion.Kind != AssetSuggestionKind.AssetKey)
                    return false;
                server.AssetKey = suggestion.Value;
                break;
            }

            case "application":
            {
                var app = config.Applications.FirstOrDefault(a => a.Id == suggestion.AssetId);
                if (app is null)
                    return false;

                switch (suggestion.Kind)
                {
                    case AssetSuggestionKind.AssetKey:
                        app.AssetKey = suggestion.Value;
                        break;
                    case AssetSuggestionKind.RuntimeSsh:
                        app.RuntimeKind = RuntimeEnvironmentKind.SshServer;
                        app.RuntimeSshAssetKey = suggestion.Value;
                        app.RuntimeDockerAssetKey = null;
                        app.RuntimeContainerName = null;
                        app.RuntimeComposeService = null;
                        break;
                    case AssetSuggestionKind.RuntimeDocker:
                        app.RuntimeKind = RuntimeEnvironmentKind.DockerApplication;
                        app.RuntimeDockerAssetKey = suggestion.Value;
                        app.RuntimeContainerName = suggestion.ContainerName;
                        app.RuntimeComposeService = suggestion.ComposeService;
                        break;
                    case AssetSuggestionKind.Url:
                        app.Urls = (app.Urls ?? Array.Empty<UrlPattern>())
                            .Append(new UrlPattern
                            {
                                Url = suggestion.Value,
                                Role = suggestion.Role,
                                Priority = 0,
                                Note = "auto suggestion"
                            })
                            .ToArray();
                        break;
                    default:
                        return false;
                }
                break;
            }

            case "datasource":
            {
                var datasource = config.DataSources.FirstOrDefault(d => d.Id == suggestion.AssetId);
                if (datasource is null)
                    return false;

                switch (suggestion.Kind)
                {
                    case AssetSuggestionKind.AssetKey:
                        datasource.AssetKey = suggestion.Value;
                        break;
                    case AssetSuggestionKind.RuntimeSsh:
                        datasource.RuntimeKind = RuntimeEnvironmentKind.SshServer;
                        datasource.RuntimeSshAssetKey = suggestion.Value;
                        datasource.RuntimeDockerAssetKey = null;
                        datasource.RuntimeContainerName = null;
                        datasource.RuntimeComposeService = null;
                        break;
                    case AssetSuggestionKind.RuntimeDocker:
                        datasource.RuntimeKind = RuntimeEnvironmentKind.DockerApplication;
                        datasource.RuntimeDockerAssetKey = suggestion.Value;
                        datasource.RuntimeContainerName = suggestion.ContainerName;
                        datasource.RuntimeComposeService = suggestion.ComposeService;
                        break;
                    default:
                        return false;
                }
                break;
            }

            default:
                return false;
        }

        ct.ThrowIfCancellationRequested();
        await _configService.SaveConfigAsync(config);
        return true;
    }

    private static SshServerConfig? FindServerForApplication(
        ApplicationConfig app,
        IReadOnlyList<SshServerConfig> servers,
        IReadOnlyDictionary<string, ServerDiscoveryEvidence?> evidenceByServer)
    {
        if (!string.IsNullOrWhiteSpace(app.ServerId))
        {
            var byId = servers.FirstOrDefault(s => s.Id == app.ServerId);
            if (byId is not null)
                return byId;
        }

        if (app.RuntimeKind == RuntimeEnvironmentKind.SshServer &&
            !string.IsNullOrWhiteSpace(app.RuntimeSshAssetKey))
        {
            var byRuntime = servers.FirstOrDefault(s =>
                string.Equals(NormalizeKey(s.AssetKey), NormalizeKey(app.RuntimeSshAssetKey),
                    StringComparison.OrdinalIgnoreCase));
            if (byRuntime is not null)
                return byRuntime;
        }

        if (!string.IsNullOrWhiteSpace(app.Host))
        {
            var byHost = servers.FirstOrDefault(s => HostMatches(s, app.Host));
            if (byHost is not null)
                return byHost;
        }

        if (!string.IsNullOrWhiteSpace(app.ContainerName))
        {
            var byContainer = servers.FirstOrDefault(s =>
                evidenceByServer.TryGetValue(s.Id, out var evidence) &&
                evidence?.Containers.Any(c => string.Equals(
                    c.Name, app.ContainerName, StringComparison.OrdinalIgnoreCase)) == true);
            if (byContainer is not null)
                return byContainer;
        }

        if (app.Port is int port)
        {
            var byPort = servers.FirstOrDefault(s =>
                evidenceByServer.TryGetValue(s.Id, out var evidence) &&
                evidence?.Ports.Any(p => p.Port == port) == true);
            if (byPort is not null)
                return byPort;
        }

        return null;
    }

    private static SshServerConfig? FindServerForDatasource(
        DataSourceConfig datasource,
        IReadOnlyList<SshServerConfig> servers,
        IReadOnlyDictionary<string, ServerDiscoveryEvidence?> evidenceByServer)
    {
        if (!string.IsNullOrWhiteSpace(datasource.TunnelServerId))
        {
            var byTunnel = servers.FirstOrDefault(s =>
                string.Equals(s.Id, datasource.TunnelServerId, StringComparison.OrdinalIgnoreCase));
            if (byTunnel is not null && IsLocal(datasource.Host))
                return byTunnel;
        }

        var byHost = servers.FirstOrDefault(s => HostMatches(s, datasource.Host));
        if (byHost is not null)
            return byHost;

        var byPort = servers.FirstOrDefault(s =>
            evidenceByServer.TryGetValue(s.Id, out var evidence) &&
            evidence?.Ports.Any(p => p.Port == datasource.Port) == true);
        return byPort;
    }

    private static bool HostMatches(SshServerConfig server, string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var value = host.Trim();
        return string.Equals(server.Host, value, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(server.Name, value, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(server.InternalHost, value, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(server.AssetKey, value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocal(string? host)
    {
        var value = (host ?? string.Empty).Trim().Trim('[', ']').ToLowerInvariant();
        return value is "localhost" or "127.0.0.1" or "::1";
    }

    private static bool IsNginx(ApplicationConfig app) =>
        string.Equals(app.Type?.Trim(), "nginx", StringComparison.OrdinalIgnoreCase) ||
        app.Name.Contains("nginx", StringComparison.OrdinalIgnoreCase);

    private static string MakeUniqueKey(
        string? name, string id, ISet<string> existing)
    {
        var baseKey = Regex.Replace((name ?? string.Empty).Trim().ToLowerInvariant(), @"[^a-z0-9\-_.]+", "-").Trim('-');
        if (baseKey.Length == 0)
            baseKey = id;
        if (existing.Add(baseKey))
            return baseKey;

        var candidate = $"{baseKey}-{id}";
        var index = 2;
        while (!existing.Add(candidate))
            candidate = $"{baseKey}-{id}-{index++}";
        return candidate;
    }

    private static string? NormalizeKey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
