namespace LitSSHmcp.Core.Models;

/// <summary>首次信任（TOFU）记录的 SSH 主机密钥指纹。</summary>
public class SshKnownHost
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string KeyAlgorithm { get; set; } = string.Empty;
    public string FingerprintSha256 { get; set; } = string.Empty;
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
}
