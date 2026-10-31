using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Topology;

/// <summary>
/// 人工配置资产拓扑的运行环境校验：不修改配置，只返回结构化问题，供编辑窗口、拓扑视图和 AI/MCP 共用。
/// </summary>
public static class AssetRuntimeValidator
{
    public static AssetRuntimeValidationResult Validate(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var result = new AssetRuntimeValidationResult();

        var servers = config.Servers ?? Array.Empty<SshServerConfig>();
        var dataSources = config.DataSources ?? Array.Empty<DataSourceConfig>();
        var applications = config.Applications ?? Array.Empty<ApplicationConfig>();

        var serversByKey = BuildAssetKeyIndex(servers);
        var applicationsByKey = BuildAssetKeyIndex(applications);

        foreach (var datasource in dataSources)
            ValidateDatasource(datasource, serversByKey, applicationsByKey, result);

        foreach (var application in applications)
            ValidateApplication(application, serversByKey, applicationsByKey, result);

        ValidateApplicationRuntimeCycles(applications, applicationsByKey, result);
        return result;
    }

    private static void ValidateDatasource(
        DataSourceConfig datasource,
        Dictionary<string, SshServerConfig> serversByKey,
        Dictionary<string, ApplicationConfig> applicationsByKey,
        AssetRuntimeValidationResult result)
    {
        if (datasource.RuntimeKind == RuntimeEnvironmentKind.Unspecified)
        {
            Add(result, datasource.Id, datasource.Name, "datasource",
                AssetRuntimeIssueSeverity.Warning, "runtime_not_configured",
                "未配置运行环境；可设置为 SSH 服务器、Docker 应用或独立环境");
            return;
        }

        if (string.IsNullOrWhiteSpace(datasource.AssetKey))
        {
            Add(result, datasource.Id, datasource.Name, "datasource",
                AssetRuntimeIssueSeverity.Warning, "asset_key_missing",
                "数据源未配置资产标识 AssetKey");
        }

        switch (datasource.RuntimeKind)
        {
            case RuntimeEnvironmentKind.SshServer:
            {
                var key = NormalizeKey(datasource.RuntimeSshAssetKey);
                if (key is null)
                {
                    Add(result, datasource.Id, datasource.Name, "datasource",
                        AssetRuntimeIssueSeverity.Error, "runtime_target_missing",
                        "运行环境为 SSH 服务器，但未选择目标服务器");
                    return;
                }

                if (!serversByKey.ContainsKey(key))
                {
                    Add(result, datasource.Id, datasource.Name, "datasource",
                        AssetRuntimeIssueSeverity.Error, "runtime_target_not_found",
                        $"运行环境目标 SSH 服务器不存在或没有 AssetKey: {datasource.RuntimeSshAssetKey}");
                }
                break;
            }

            case RuntimeEnvironmentKind.DockerApplication:
            {
                var key = NormalizeKey(datasource.RuntimeDockerAssetKey);
                if (key is null)
                {
                    Add(result, datasource.Id, datasource.Name, "datasource",
                        AssetRuntimeIssueSeverity.Error, "runtime_target_missing",
                        "运行环境为 Docker 应用，但未选择目标应用");
                    return;
                }

                if (!applicationsByKey.TryGetValue(key, out var target))
                {
                    Add(result, datasource.Id, datasource.Name, "datasource",
                        AssetRuntimeIssueSeverity.Error, "runtime_target_not_found",
                        $"运行环境目标 Docker 应用不存在或没有 AssetKey: {datasource.RuntimeDockerAssetKey}");
                }
                else if (!IsDockerApplication(target))
                {
                    Add(result, datasource.Id, datasource.Name, "datasource",
                        AssetRuntimeIssueSeverity.Error, "runtime_target_type_mismatch",
                        $"运行环境目标应用不是 Docker 类型: {target.Name}");
                }

                if (string.IsNullOrWhiteSpace(datasource.RuntimeContainerName) &&
                    string.IsNullOrWhiteSpace(datasource.RuntimeComposeService))
                {
                    Add(result, datasource.Id, datasource.Name, "datasource",
                        AssetRuntimeIssueSeverity.Error, "docker_runtime_container_required",
                        "运行环境为 Docker 应用，必须填写容器名或 Compose service");
                }
                break;
            }
        }
    }

    private static void ValidateApplication(
        ApplicationConfig application,
        Dictionary<string, SshServerConfig> serversByKey,
        Dictionary<string, ApplicationConfig> applicationsByKey,
        AssetRuntimeValidationResult result)
    {
        if (application.RuntimeKind == RuntimeEnvironmentKind.Unspecified)
        {
            Add(result, application.Id, application.Name, "application",
                AssetRuntimeIssueSeverity.Warning, "runtime_not_configured",
                "未配置运行环境；可设置为 SSH 服务器、Docker 应用或独立环境");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(application.AssetKey))
            {
                Add(result, application.Id, application.Name, "application",
                    AssetRuntimeIssueSeverity.Warning, "asset_key_missing",
                    "应用未配置资产标识 AssetKey");
            }

            switch (application.RuntimeKind)
            {
                case RuntimeEnvironmentKind.SshServer:
                {
                    var key = NormalizeKey(application.RuntimeSshAssetKey);
                    if (key is null)
                    {
                        Add(result, application.Id, application.Name, "application",
                            AssetRuntimeIssueSeverity.Error, "runtime_target_missing",
                            "运行环境为 SSH 服务器，但未选择目标服务器");
                    }
                    else if (!serversByKey.ContainsKey(key))
                    {
                        Add(result, application.Id, application.Name, "application",
                            AssetRuntimeIssueSeverity.Error, "runtime_target_not_found",
                            $"运行环境目标 SSH 服务器不存在或没有 AssetKey: {application.RuntimeSshAssetKey}");
                    }
                    break;
                }

                case RuntimeEnvironmentKind.DockerApplication:
                {
                    var key = NormalizeKey(application.RuntimeDockerAssetKey);
                    if (key is null)
                    {
                        Add(result, application.Id, application.Name, "application",
                            AssetRuntimeIssueSeverity.Error, "runtime_target_missing",
                            "运行环境为 Docker 应用，但未选择目标应用");
                        break;
                    }

                    if (IsDockerApplication(application))
                    {
                        Add(result, application.Id, application.Name, "application",
                            AssetRuntimeIssueSeverity.Error, "docker_nested_not_allowed",
                            "Docker 类型应用不能运行在 Docker 应用环境中");
                    }

                    if (!applicationsByKey.TryGetValue(key, out var target))
                    {
                        Add(result, application.Id, application.Name, "application",
                            AssetRuntimeIssueSeverity.Error, "runtime_target_not_found",
                            $"运行环境目标 Docker 应用不存在或没有 AssetKey: {application.RuntimeDockerAssetKey}");
                    }
                    else
                    {
                        if (!IsDockerApplication(target))
                        {
                            Add(result, application.Id, application.Name, "application",
                                AssetRuntimeIssueSeverity.Error, "runtime_target_type_mismatch",
                                $"运行环境目标应用不是 Docker 类型: {target.Name}");
                        }

                        if (string.Equals(NormalizeKey(application.AssetKey), key, StringComparison.OrdinalIgnoreCase))
                        {
                            Add(result, application.Id, application.Name, "application",
                                AssetRuntimeIssueSeverity.Error, "runtime_self_reference",
                                "应用不能以自身作为 Docker 运行环境");
                        }
                    }

                    if (string.IsNullOrWhiteSpace(application.RuntimeContainerName) &&
                        string.IsNullOrWhiteSpace(application.RuntimeComposeService))
                    {
                        Add(result, application.Id, application.Name, "application",
                            AssetRuntimeIssueSeverity.Error, "docker_runtime_container_required",
                            "运行环境为 Docker 应用，必须填写容器名或 Compose service");
                    }
                    break;
                }
            }
        }

        foreach (var url in application.Urls ?? Array.Empty<UrlPattern>())
        {
            if (string.IsNullOrWhiteSpace(url.Url))
            {
                Add(result, application.Id, application.Name, "application",
                    AssetRuntimeIssueSeverity.Error, "url_empty",
                    "URL 配置不能为空");
                continue;
            }

            if (!IsValidUrlPattern(url.Url))
            {
                Add(result, application.Id, application.Name, "application",
                    AssetRuntimeIssueSeverity.Error, "url_invalid",
                    $"URL 格式无效: {url.Url}");
            }
        }
    }

    private static void ValidateApplicationRuntimeCycles(
        IReadOnlyList<ApplicationConfig> applications,
        Dictionary<string, ApplicationConfig> applicationsByKey,
        AssetRuntimeValidationResult result)
    {
        var edges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var application in applications)
        {
            if (application.RuntimeKind != RuntimeEnvironmentKind.DockerApplication)
                continue;

            var from = NormalizeKey(application.AssetKey);
            var to = NormalizeKey(application.RuntimeDockerAssetKey);
            if (from is not null && to is not null && applicationsByKey.ContainsKey(to))
                edges[from] = to;
        }

        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();

        void Visit(string node)
        {
            if (state.TryGetValue(node, out var current))
            {
                if (current == 1)
                {
                    var start = stack.FindIndex(x => string.Equals(x, node, StringComparison.OrdinalIgnoreCase));
                    var cycle = start >= 0 ? stack.Skip(start).ToArray() : stack.ToArray();
                    foreach (var key in cycle)
                    {
                        if (applicationsByKey.TryGetValue(key, out var app))
                        {
                            Add(result, app.Id, app.Name, "application",
                                AssetRuntimeIssueSeverity.Error, "runtime_cycle",
                                "运行环境存在循环：" + string.Join(" -> ", cycle.Append(cycle[0])));
                        }
                    }
                }
                return;
            }

            state[node] = 1;
            stack.Add(node);
            if (edges.TryGetValue(node, out var next))
                Visit(next);
            stack.RemoveAt(stack.Count - 1);
            state[node] = 2;
        }

        foreach (var key in edges.Keys)
            Visit(key);
    }

    private static Dictionary<string, SshServerConfig> BuildAssetKeyIndex(
        IEnumerable<SshServerConfig> servers)
    {
        var result = new Dictionary<string, SshServerConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in servers)
        {
            var key = NormalizeKey(server.AssetKey);
            if (key is not null && !result.ContainsKey(key))
                result[key] = server;
        }
        return result;
    }

    private static Dictionary<string, ApplicationConfig> BuildAssetKeyIndex(
        IEnumerable<ApplicationConfig> applications)
    {
        var result = new Dictionary<string, ApplicationConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var application in applications)
        {
            var key = NormalizeKey(application.AssetKey);
            if (key is not null && !result.ContainsKey(key))
                result[key] = application;
        }
        return result;
    }

    private static bool IsDockerApplication(ApplicationConfig application) =>
        string.Equals(application.Type?.Trim(), "docker", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : key.Trim();

    private static bool IsValidUrlPattern(string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
            return false;

        var candidate = text.Contains("://", StringComparison.Ordinal)
            ? text
            : "https://" + text.TrimStart('/');

        var hostStart = candidate.IndexOf("://", StringComparison.Ordinal) + 3;
        var slash = candidate.IndexOf('/', hostStart);
        var hostEnd = slash >= 0 ? slash : candidate.Length;
        if (hostStart < 3 || hostEnd <= hostStart)
            return false;

        var host = candidate[hostStart..hostEnd];
        if (host.Contains('*'))
            candidate = candidate[..hostStart] + host.Replace("*", "wildcard", StringComparison.Ordinal) + candidate[hostEnd..];

        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static void Add(
        AssetRuntimeValidationResult result,
        string assetId,
        string assetName,
        string assetType,
        AssetRuntimeIssueSeverity severity,
        string code,
        string message)
    {
        result.Issues.Add(new AssetRuntimeIssue
        {
            AssetId = assetId,
            AssetName = assetName,
            AssetType = assetType,
            Severity = severity,
            Code = code,
            Message = message
        });
    }
}
