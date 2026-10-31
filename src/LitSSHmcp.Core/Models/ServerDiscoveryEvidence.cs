namespace LitSSHmcp.Core.Models;

public sealed class ServerDiscoveryEvidence
{
    public string ServerId { get; set; } = string.Empty;
    public long? SnapshotId { get; set; }
    public string? CapturedAt { get; set; }
    public List<DiscoveryPortEvidence> Ports { get; } = new();
    public List<DiscoveryServiceEvidence> Services { get; } = new();
    public List<DiscoveryContainerEvidence> Containers { get; } = new();
    public List<DiscoveryDomainEvidence> Domains { get; } = new();
    public List<string> Notes { get; } = new();
}

public sealed class DiscoveryPortEvidence
{
    public string Protocol { get; set; } = "tcp";
    public int Port { get; set; }
    public string? Bind { get; set; }
    public string? Process { get; set; }
    public int? Pid { get; set; }
    public string? Unit { get; set; }
    public bool SystemdManaged { get; set; }
    public string? Executable { get; set; }
    public string? CommandLine { get; set; }
}

public sealed class DiscoveryServiceEvidence
{
    public string Unit { get; set; } = string.Empty;
    public string? Active { get; set; }
    public string? Sub { get; set; }
    public string? Description { get; set; }
}

public sealed class DiscoveryContainerEvidence
{
    public string Name { get; set; } = string.Empty;
    public string? Image { get; set; }
    public string? State { get; set; }
    public string? Status { get; set; }
    public string? Ports { get; set; }
    public string? ComposeProject { get; set; }
    public string? ComposeService { get; set; }
    public string? Labels { get; set; }
}

public sealed class DiscoveryDomainEvidence
{
    public string Domain { get; set; } = string.Empty;
    public bool Ssl { get; set; }
    public int[] Ports { get; set; } = Array.Empty<int>();
    public List<DiscoveryCertificateEvidence> Certificates { get; } = new();
}

public sealed class DiscoveryCertificateEvidence
{
    public string? Path { get; set; }
    public string? SubjectCn { get; set; }
    public string? ExpiresAt { get; set; }
    public string? Fingerprint { get; set; }
}
