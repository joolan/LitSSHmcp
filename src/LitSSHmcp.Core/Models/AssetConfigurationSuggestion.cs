namespace LitSSHmcp.Core.Models;

public enum AssetSuggestionKind
{
    AssetKey,
    RuntimeSsh,
    RuntimeDocker,
    DockerContainer,
    Url
}

public sealed class AssetConfigurationSuggestion
{
    public string AssetId { get; set; } = string.Empty;
    public string AssetType { get; set; } = string.Empty;
    public string AssetName { get; set; } = string.Empty;
    public AssetSuggestionKind Kind { get; set; }
    public string Value { get; set; } = string.Empty;
    public string? SecondaryValue { get; set; }
    public string? ContainerName { get; set; }
    public string? ComposeService { get; set; }
    public UrlRole Role { get; set; } = UrlRole.Auto;
    public string Reason { get; set; } = string.Empty;
    public double Confidence { get; set; } = 0.8;
}

public enum AssetConfigurationIssueSeverity
{
    Info,
    Warning,
    Error
}

public sealed class AssetConfigurationIssue
{
    public string AssetId { get; set; } = string.Empty;
    public string AssetType { get; set; } = string.Empty;
    public string AssetName { get; set; } = string.Empty;
    public AssetConfigurationIssueSeverity Severity { get; set; } = AssetConfigurationIssueSeverity.Warning;
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
