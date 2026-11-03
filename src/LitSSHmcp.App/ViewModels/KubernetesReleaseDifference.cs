namespace LitSSHmcp.App.ViewModels;

public sealed class KubernetesReleaseDifference
{
    public string Namespace { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string BaselineImage { get; init; } = string.Empty;
    public string TargetImage { get; init; } = string.Empty;
    public string BaselineReplicas { get; init; } = string.Empty;
    public string TargetReplicas { get; init; } = string.Empty;
    public string BaselineResources { get; init; } = string.Empty;
    public string TargetResources { get; init; } = string.Empty;
    public string Difference { get; init; } = string.Empty;
    public bool IsDifferent { get; init; }
}

public sealed class KubernetesIngressDifference
{
    public string Namespace { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string BaselineClass { get; init; } = string.Empty;
    public string TargetClass { get; init; } = string.Empty;
    public string BaselineHosts { get; init; } = string.Empty;
    public string TargetHosts { get; init; } = string.Empty;
    public string Difference { get; init; } = string.Empty;
    public bool IsDifferent { get; init; }
}
