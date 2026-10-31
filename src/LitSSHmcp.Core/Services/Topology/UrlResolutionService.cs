using System.Text.RegularExpressions;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.Topology;

public interface IUrlResolutionService
{
    Task<UrlResolutionResult> ResolveAsync(string url, CancellationToken ct = default);
}

/// <summary>
/// 根据人工配置的应用 URL 解析入口应用、后端应用和最终运行环境。
/// 不访问远端、不依赖自动发现结果。
/// </summary>
public sealed class UrlResolutionService : IUrlResolutionService
{
    private static readonly Regex ExplicitPortPattern = new(
        @"^(?:https?://)?[^/\s]+:(\d+)(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IConfigService _configService;

    public UrlResolutionService(IConfigService configService)
    {
        _configService = configService;
    }

    public async Task<UrlResolutionResult> ResolveAsync(string url, CancellationToken ct = default)
    {
        var config = await _configService.LoadConfigAsync();
        return Resolve(config, url);
    }

    public static UrlResolutionResult Resolve(AppConfig config, string url)
    {
        var result = new UrlResolutionResult { InputUrl = url ?? string.Empty };
        if (!TryParse(url, out var request, out var normalized))
        {
            result.Success = false;
            result.Error = "invalid_url";
            result.Summary = "URL 无效，必须是 http/https 绝对地址";
            return result;
        }

        result.NormalizedUrl = normalized;
        var matches = new List<(ApplicationConfig App, UrlPattern Pattern, int Score)>();
        foreach (var app in config.Applications ?? Array.Empty<ApplicationConfig>())
        {
            foreach (var pattern in app.Urls ?? Array.Empty<UrlPattern>())
            {
                if (TryMatch(pattern.Url, pattern.Priority, request, out var score))
                    matches.Add((app, pattern, score));
            }
        }

        var best = matches
            .GroupBy(m => m.App.Id, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(m => m.Score).First())
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.App.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (best.Count == 0)
        {
            result.Success = false;
            result.Error = "url_not_found";
            result.Summary = $"未找到匹配 {normalized} 的应用 URL";
            return result;
        }

        var entries = best
            .Where(m => m.Pattern.Role == UrlRole.PublicEntry)
            .Select(m => ToMatched(m.App, m.Pattern, m.Score, UrlRole.PublicEntry))
            .ToList();
        var backends = best
            .Where(m => m.Pattern.Role == UrlRole.Backend)
            .Select(m => ToMatched(m.App, m.Pattern, m.Score, UrlRole.Backend))
            .ToList();
        var autos = best
            .Where(m => m.Pattern.Role == UrlRole.Auto)
            .Select(m => ToMatched(m.App, m.Pattern, m.Score, UrlRole.Auto))
            .ToList();

        result.EntryApps = entries;
        result.BackendApps = backends;
        result.LandingApps = backends.Count > 0
            ? backends
            : autos.Count > 0
                ? autos
                : entries;

        if (result.LandingApps.Count > 1 &&
            result.LandingApps.All(a => a.Role == UrlRole.PublicEntry))
        {
            result.Ambiguities.Add("多个 PublicEntry 应用命中同一 URL，请检查入口配置或设置优先级");
        }

        foreach (var app in result.LandingApps)
        {
            var configApp = config.Applications?.FirstOrDefault(a => a.Id == app.Id);
            if (configApp is not null)
                result.RuntimeChains.Add(BuildRuntimeChain(config, configApp));
        }

        result.Summary = BuildSummary(result, best);
        return result;
    }

    private static UrlMatchedApp ToMatched(
        ApplicationConfig app, UrlPattern pattern, int score, UrlRole role) => new()
    {
        Id = app.Id,
        Name = app.Name,
        Type = app.Type,
        AssetKey = app.AssetKey,
        Role = role,
        MatchedPattern = pattern.Url,
        Score = score,
        Priority = pattern.Priority,
        LogPaths = app.LogPaths ?? Array.Empty<string>()
    };

    private static string BuildSummary(
        UrlResolutionResult result,
        IReadOnlyList<(ApplicationConfig App, UrlPattern Pattern, int Score)> matches)
    {
        var landing = result.LandingApps.Count == 0
            ? "无落地应用"
            : string.Join(", ", result.LandingApps.Select(a => a.Name));
        var entry = result.EntryApps.Count == 0
            ? "无入口应用"
            : string.Join(", ", result.EntryApps.Select(a => a.Name));
        var servers = result.RuntimeChains
            .Select(c => c.ServerName ?? c.ServerAssetKey)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return $"{result.NormalizedUrl} -> 入口: {entry}；落地: {landing}" +
               (servers.Length > 0 ? $"；服务器: {string.Join(", ", servers)}" : string.Empty) +
               (result.Ambiguities.Count > 0 ? $"；歧义 {result.Ambiguities.Count} 条" : string.Empty);
    }

    private static RuntimeChain BuildRuntimeChain(AppConfig config, ApplicationConfig app)
    {
        var chain = new RuntimeChain
        {
            ApplicationId = app.Id,
            ApplicationName = app.Name
        };
        var steps = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = app;

        for (var depth = 0; depth < 6; depth++)
        {
            if (current is null || !visited.Add(current.Id))
            {
                chain.Error = "运行环境存在循环";
                break;
            }

            switch (current.RuntimeKind)
            {
                case RuntimeEnvironmentKind.SshServer:
                {
                    var server = FindServer(config, current.RuntimeSshAssetKey);
                    if (server is null)
                    {
                        chain.Error = $"找不到 SSH 运行环境: {current.RuntimeSshAssetKey}";
                        steps.Add($"{current.Name} --runsOn--> {current.RuntimeSshAssetKey ?? "?"} (未找到)");
                        chain.Steps = steps.ToArray();
                        return chain;
                    }

                    chain.ServerId = server.Id;
                    chain.ServerAssetKey = server.AssetKey;
                    chain.ServerName = server.Name;
                    chain.ServerHost = server.Host;
                    steps.Add($"{current.Name} --runsOn--> {server.Name}");
                    chain.Complete = true;
                    chain.Steps = steps.ToArray();
                    return chain;
                }

                case RuntimeEnvironmentKind.DockerApplication:
                {
                    if (string.IsNullOrWhiteSpace(current.RuntimeDockerAssetKey))
                    {
                        chain.Error = "Docker 运行环境未配置目标应用";
                        break;
                    }

                    var docker = FindDockerApplication(config, current.RuntimeDockerAssetKey);
                    if (docker is null)
                    {
                        chain.Error = $"找不到 Docker 应用: {current.RuntimeDockerAssetKey}";
                        steps.Add($"{current.Name} --runsIn--> {current.RuntimeDockerAssetKey} (未找到)");
                        chain.Steps = steps.ToArray();
                        return chain;
                    }

                    chain.DockerApplicationId = docker.Id;
                    chain.DockerApplicationAssetKey = docker.AssetKey;
                    chain.DockerApplicationName = docker.Name;
                    chain.ContainerName = current.RuntimeContainerName;
                    chain.ComposeService = current.RuntimeComposeService;
                    steps.Add($"{current.Name} --runsIn--> {docker.Name}");
                    current = docker;
                    continue;
                }

                case RuntimeEnvironmentKind.Standalone:
                    steps.Add($"{current.Name} --standalone-->");
                    chain.Complete = true;
                    chain.Steps = steps.ToArray();
                    return chain;

                default:
                    chain.Error = "未配置运行环境";
                    chain.Steps = steps.ToArray();
                    return chain;
            }
        }

        chain.Steps = steps.ToArray();
        return chain;
    }

    private static SshServerConfig? FindServer(AppConfig config, string? assetKey)
    {
        var key = NormalizeKey(assetKey);
        return key is null
            ? null
            : config.Servers?.FirstOrDefault(s =>
                string.Equals(NormalizeKey(s.AssetKey), key, StringComparison.OrdinalIgnoreCase));
    }

    private static ApplicationConfig? FindDockerApplication(AppConfig config, string? assetKey)
    {
        var key = NormalizeKey(assetKey);
        return key is null
            ? null
            : config.Applications?.FirstOrDefault(a =>
                string.Equals(NormalizeKey(a.AssetKey), key, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Type?.Trim(), "docker", StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeKey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryMatch(string? patternValue, int priority, ParsedUrl request, out int score)
    {
        score = 0;
        if (!TryParse(patternValue, out var pattern, out _))
            return false;

        if (pattern.HadScheme && !string.Equals(pattern.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        if (pattern.HasExplicitPort && pattern.Port != request.Port)
            return false;

        int hostScore;
        if (pattern.WildcardHost)
        {
            var suffix = pattern.Host.StartsWith("wildcard.", StringComparison.OrdinalIgnoreCase)
                ? pattern.Host["wildcard.".Length..]
                : pattern.Host;
            if (!request.Host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                return false;
            hostScore = 50_000;
        }
        else
        {
            if (!string.Equals(pattern.Host, request.Host, StringComparison.OrdinalIgnoreCase))
                return false;
            hostScore = 100_000;
        }

        if (!PathMatches(pattern.Path, request.Path))
            return false;

        score = hostScore + pattern.Path.Length * 10 + priority;
        return true;
    }

    private static bool PathMatches(string patternPath, string requestPath)
    {
        var pattern = string.IsNullOrEmpty(patternPath) ? "/" : patternPath;
        if (pattern == "/")
            return true;

        var normalizedPattern = pattern.Length > 1 ? pattern.TrimEnd('/') : pattern;
        var normalizedRequest = requestPath.Length > 1 ? requestPath.TrimEnd('/') : requestPath;

        return string.Equals(normalizedRequest, normalizedPattern, StringComparison.Ordinal) ||
               normalizedRequest.StartsWith(normalizedPattern + "/", StringComparison.Ordinal);
    }

    private static bool TryParse(string? value, out ParsedUrl parsed, out string? normalized)
    {
        parsed = default;
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        var hadScheme = text.Contains("://", StringComparison.Ordinal);
        var wildcardHost = false;
        var raw = hadScheme ? text : "https://" + text.TrimStart('/');
        var hostStart = raw.IndexOf("://", StringComparison.Ordinal) + 3;
        var hostEnd = raw.IndexOf('/', hostStart);
        if (hostEnd < 0)
            hostEnd = raw.Length;

        var hostPart = raw[hostStart..hostEnd];
        if (hostPart.StartsWith("*.", StringComparison.Ordinal))
        {
            wildcardHost = true;
            raw = raw[..hostStart] + "wildcard." + hostPart[2..] + raw[hostEnd..];
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;

        var hasExplicitPort = ExplicitPortPattern.IsMatch(text);
        var path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        parsed = new ParsedUrl(
            uri.Scheme.ToLowerInvariant(),
            uri.Host.ToLowerInvariant().TrimEnd('.'),
            uri.Port,
            path,
            hadScheme,
            hasExplicitPort,
            wildcardHost);

        normalized = $"{parsed.Scheme}://{parsed.Host}" +
                     (hasExplicitPort ? $":{parsed.Port}" : string.Empty) +
                     parsed.Path.TrimEnd('/');
        if (normalized.EndsWith("://", StringComparison.Ordinal))
            normalized += "/";
        return true;
    }

    private readonly record struct ParsedUrl(
        string Scheme,
        string Host,
        int Port,
        string Path,
        bool HadScheme,
        bool HasExplicitPort,
        bool WildcardHost);
}
