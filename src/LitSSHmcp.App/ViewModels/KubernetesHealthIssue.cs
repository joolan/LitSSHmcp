namespace LitSSHmcp.App.ViewModels;

public sealed class KubernetesHealthIssue
{
    public string Severity { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Namespace { get; init; } = string.Empty;
    public string Object { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
