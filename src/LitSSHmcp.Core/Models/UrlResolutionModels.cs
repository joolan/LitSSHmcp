namespace LitSSHmcp.Core.Models;

public sealed class UrlResolutionResult
{
    public bool Success { get; set; } = true;
    public string InputUrl { get; set; } = string.Empty;
    public string? NormalizedUrl { get; set; }
    public List<UrlMatchedApp> EntryApps { get; set; } = new();
    public List<UrlMatchedApp> BackendApps { get; set; } = new();
    public List<UrlMatchedApp> LandingApps { get; set; } = new();
    public List<RuntimeChain> RuntimeChains { get; set; } = new();
    public List<string> Ambiguities { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
    public string? Error { get; set; }
}

public sealed class UrlMatchedApp
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? AssetKey { get; set; }
    public UrlRole Role { get; set; }
    public string MatchedPattern { get; set; } = string.Empty;
    public int Score { get; set; }
    public int Priority { get; set; }
    public string[] LogPaths { get; set; } = Array.Empty<string>();
}

public sealed class RuntimeChain
{
    public string ApplicationId { get; set; } = string.Empty;
    public string ApplicationName { get; set; } = string.Empty;
    public string? DockerApplicationId { get; set; }
    public string? DockerApplicationAssetKey { get; set; }
    public string? DockerApplicationName { get; set; }
    public string? ContainerName { get; set; }
    public string? ComposeService { get; set; }
    public string? ServerId { get; set; }
    public string? ServerAssetKey { get; set; }
    public string? ServerName { get; set; }
    public string? ServerHost { get; set; }
    public string[] Steps { get; set; } = Array.Empty<string>();
    public bool Complete { get; set; }
    public string? Error { get; set; }
}
