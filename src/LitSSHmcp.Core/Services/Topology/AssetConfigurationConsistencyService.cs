using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.Topology;

public interface IAssetConfigurationConsistencyService
{
    Task<List<AssetConfigurationIssue>> GetIssuesAsync(CancellationToken ct = default);
}

/// <summary>
/// 对照最近快照证据检查人工配置是否“仍能被发现”：运行环境缺失、端口/进程/容器/URL 未观察到。
/// 只读，不修改配置。
/// </summary>
public sealed class AssetConfigurationConsistencyService : IAssetConfigurationConsistencyService
{
    private readonly IConfigService _configService;
    private readonly ITopologyService _topologyService;

    public AssetConfigurationConsistencyService(
        IConfigService configService, ITopologyService topologyService)
    {
        _configService = configService;
        _topologyService = topologyService;
    }

    public async Task<List<AssetConfigurationIssue>> GetIssuesAsync(CancellationToken ct = default)
    {
        var config = await _configService.LoadConfigAsync();
        var servers = config.Servers ?? Array.Empty<SshServerConfig>();
        var evidenceByServer = new Dictionary<string, ServerDiscoveryEvidence?>(StringComparer.OrdinalIgnoreCase);

        foreach (var server in servers)
        {
            ct.ThrowIfCancellationRequested();
            evidenceByServer[server.Id] = await _topologyService.GetServerDiscoveryEvidenceAsync(server.Id, ct);
        }

        var issues = new List<AssetConfigurationIssue>();

        foreach (var application in config.Applications ?? Array.Empty<ApplicationConfig>())
        {
            ct.ThrowIfCancellationRequested();
            var server = ResolveServerForApplication(application, config, servers, evidenceByServer);
            if (server is null)
                continue;

            var evidence = evidenceByServer.GetValueOrDefault(server.Id);
            if (evidence is null)
            {
                Add(issues, application.Id, application.Name, "application", "no_discovery_evidence",
                    $"服务器 {server.Name} 暂无快照/发现证据，无法核对运行环境");
                continue;
            }

            if (!ApplicationObserved(application, evidence))
            {
                Add(issues, application.Id, application.Name, "application", "application_not_observed",
                    $"在服务器 {server.Name} 的发现证据中未找到应用 {application.Name} 的进程/容器/端口");
            }

            CheckUrls(application, evidence, issues);
        }

        foreach (var datasource in config.DataSources ?? Array.Empty<DataSourceConfig>())
        {
            ct.ThrowIfCancellationRequested();
            var server = ResolveServerForDatasource(datasource, config, servers);
            if (server is null)
                continue;

            var evidence = evidenceByServer.GetValueOrDefault(server.Id);
            if (evidence is null)
            {
                Add(issues, datasource.Id, datasource.Name, "datasource", "no_discovery_evidence",
                    $"服务器 {server.Name} 暂无快照/发现证据，无法核对数据源运行环境");
                continue;
            }

            var portObserved = evidence.Ports.Any(p => p.Port == datasource.Port);
            if (!portObserved)
            {
                Add(issues, datasource.Id, datasource.Name, "datasource", "datasource_not_observed",
                    $"服务器 {server.Name} 的发现证据中未监听端口 {datasource.Port}");
            }

            if (datasource.RuntimeKind == RuntimeEnvironmentKind.DockerApplication &&
                (!string.IsNullOrWhiteSpace(datasource.RuntimeContainerName) ||
                 !string.IsNullOrWhiteSpace(datasource.RuntimeComposeService)) &&
                !ContainerObserved(datasource.RuntimeContainerName, datasource.RuntimeComposeService, evidence))
            {
                Add(issues, datasource.Id, datasource.Name, "datasource", "datasource_container_not_observed",
                    $"服务器 {server.Name} 的发现证据中未找到数据源容器/Compose service");
            }
        }

        return issues
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.AssetType, StringComparer.Ordinal)
            .ThenBy(i => i.AssetName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ApplicationObserved(ApplicationConfig app, ServerDiscoveryEvidence evidence)
    {
        if (app.RuntimeKind == RuntimeEnvironmentKind.DockerApplication &&
            (!string.IsNullOrWhiteSpace(app.RuntimeContainerName) ||
             !string.IsNullOrWhiteSpace(app.RuntimeComposeService)))
        {
            return ContainerObserved(app.RuntimeContainerName, app.RuntimeComposeService, evidence);
        }

        if (!string.IsNullOrWhiteSpace(app.ContainerName) &&
            evidence.Containers.Any(c => string.Equals(
                c.Name, app.ContainerName, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (app.Port is int port && evidence.Ports.Any(p => p.Port == port))
            return true;

        if (!string.IsNullOrWhiteSpace(app.Path) &&
            evidence.Ports.Any(p =>
                (!string.IsNullOrWhiteSpace(p.Executable) &&
                 p.Executable.Contains(app.Path, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(p.CommandLine) &&
                 p.CommandLine.Contains(app.Path, StringComparison.OrdinalIgnoreCase))))
            return true;

        return !string.IsNullOrWhiteSpace(app.Name) &&
               evidence.Ports.Any(p =>
                   (!string.IsNullOrWhiteSpace(p.Process) &&
                    p.Process.Contains(app.Name, StringComparison.OrdinalIgnoreCase)) ||
                   (!string.IsNullOrWhiteSpace(p.CommandLine) &&
                    p.CommandLine.Contains(app.Name, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool ContainerObserved(
        string? containerName, string? composeService, ServerDiscoveryEvidence evidence)
    {
        if (!string.IsNullOrWhiteSpace(containerName) &&
            evidence.Containers.Any(c => string.Equals(
                c.Name, containerName, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (!string.IsNullOrWhiteSpace(composeService) &&
            evidence.Containers.Any(c => string.Equals(
                c.ComposeService, composeService, StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    private static void CheckUrls(
        ApplicationConfig app,
        ServerDiscoveryEvidence evidence,
        List<AssetConfigurationIssue> issues)
    {
        foreach (var pattern in app.Urls ?? Array.Empty<UrlPattern>())
        {
            var host = TryGetHost(pattern.Url);
            if (string.IsNullOrWhiteSpace(host))
                continue;

            if (!evidence.Domains.Any(d => DomainMatches(d.Domain, host)))
            {
                Add(issues, app.Id, app.Name, "application", "url_not_observed",
                    $"URL {pattern.Url} 的域名 {host} 未在服务器 {evidence.ServerId} 的 Nginx 站点证据中出现");
            }
        }
    }

    private static bool DomainMatches(string domain, string host)
    {
        if (string.Equals(domain, host, StringComparison.OrdinalIgnoreCase))
            return true;
        if (domain.StartsWith("*.", StringComparison.Ordinal))
            return host.EndsWith(domain[1..], StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static string? TryGetHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Contains("://", StringComparison.Ordinal)
            ? value
            : "https://" + value.TrimStart('/');
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            ? uri.Host.ToLowerInvariant()
            : null;
    }

    private static SshServerConfig? ResolveServerForApplication(
        ApplicationConfig app,
        AppConfig config,
        IReadOnlyList<SshServerConfig> servers,
        IReadOnlyDictionary<string, ServerDiscoveryEvidence?> evidenceByServer)
    {
        if (app.RuntimeKind == RuntimeEnvironmentKind.SshServer)
            return FindServerByKey(servers, app.RuntimeSshAssetKey);

        if (app.RuntimeKind == RuntimeEnvironmentKind.DockerApplication)
        {
            var docker = FindDockerApp(config, app.RuntimeDockerAssetKey);
            if (docker is null)
                return null;
            return ResolveServerForApplication(docker, config, servers, evidenceByServer);
        }

        if (!string.IsNullOrWhiteSpace(app.ServerId))
            return servers.FirstOrDefault(s => s.Id == app.ServerId);

        return servers.FirstOrDefault(s => HostMatches(s, app.Host));
    }

    private static SshServerConfig? ResolveServerForDatasource(
        DataSourceConfig datasource,
        AppConfig config,
        IReadOnlyList<SshServerConfig> servers)
    {
        if (datasource.RuntimeKind == RuntimeEnvironmentKind.SshServer)
            return FindServerByKey(servers, datasource.RuntimeSshAssetKey);

        if (datasource.RuntimeKind == RuntimeEnvironmentKind.DockerApplication)
        {
            var docker = FindDockerApp(config, datasource.RuntimeDockerAssetKey);
            return docker is null
                ? null
                : ResolveServerForApplication(docker, config, servers,
                    new Dictionary<string, ServerDiscoveryEvidence?>(StringComparer.OrdinalIgnoreCase));
        }

        return servers.FirstOrDefault(s => HostMatches(s, datasource.Host));
    }

    private static SshServerConfig? FindServerByKey(
        IReadOnlyList<SshServerConfig> servers, string? key)
    {
        var normalized = NormalizeKey(key);
        return normalized is null
            ? null
            : servers.FirstOrDefault(s =>
                string.Equals(NormalizeKey(s.AssetKey), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static ApplicationConfig? FindDockerApp(AppConfig config, string? key)
    {
        var normalized = NormalizeKey(key);
        return normalized is null
            ? null
            : config.Applications?.FirstOrDefault(a =>
                string.Equals(NormalizeKey(a.AssetKey), normalized, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Type?.Trim(), "docker", StringComparison.OrdinalIgnoreCase));
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

    private static string? NormalizeKey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Add(
        List<AssetConfigurationIssue> issues,
        string assetId,
        string assetName,
        string assetType,
        string code,
        string message)
    {
        issues.Add(new AssetConfigurationIssue
        {
            AssetId = assetId,
            AssetName = assetName,
            AssetType = assetType,
            Code = code,
            Message = message,
            Severity = AssetConfigurationIssueSeverity.Warning
        });
    }
}
