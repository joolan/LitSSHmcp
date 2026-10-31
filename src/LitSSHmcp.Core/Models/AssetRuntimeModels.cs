namespace LitSSHmcp.Core.Models;

public enum RuntimeEnvironmentKind
{
    Unspecified = 0,
    SshServer,
    DockerApplication,
    Standalone
}

public enum UrlRole
{
    Auto = 0,
    PublicEntry,
    Backend,
    Internal
}

public sealed class UrlPattern
{
    public string Url { get; set; } = string.Empty;
    public UrlRole Role { get; set; } = UrlRole.Auto;
    public int Priority { get; set; }
    public string? Note { get; set; }
}

public enum AssetRuntimeIssueSeverity
{
    Warning,
    Error
}

public sealed class AssetRuntimeIssue
{
    public string AssetType { get; init; } = string.Empty;
    public string AssetId { get; init; } = string.Empty;
    public string AssetName { get; init; } = string.Empty;
    public AssetRuntimeIssueSeverity Severity { get; init; } = AssetRuntimeIssueSeverity.Warning;
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed class AssetRuntimeValidationResult
{
    public List<AssetRuntimeIssue> Issues { get; } = new();
    public bool HasErrors => Issues.Any(i => i.Severity == AssetRuntimeIssueSeverity.Error);
    public bool HasWarnings => Issues.Any(i => i.Severity == AssetRuntimeIssueSeverity.Warning);
}
