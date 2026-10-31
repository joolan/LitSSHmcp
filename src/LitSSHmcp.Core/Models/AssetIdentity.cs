namespace LitSSHmcp.Core.Models;

public static class AssetIdentityKinds
{
    public const string SshMachineId = "ssh-machine-id";
    public const string SshHostKey = "ssh-host-key";
    public const string SshHostname = "ssh-hostname";
    public const string MysqlServerUuid = "mysql-server-uuid";
    public const string PostgresSystemIdentifier = "pg-system-identifier";
    public const string RedisClusterId = "redis-cluster-id";

    public static bool IsStrong(string kind) => kind is
        SshMachineId or SshHostKey or MysqlServerUuid or PostgresSystemIdentifier or RedisClusterId;
}

public class AssetIdentityRecord
{
    public string AssetId { get; set; } = string.Empty;
    public string AssetType { get; set; } = string.Empty;
    public string IdentityKind { get; set; } = string.Empty;
    public string IdentityValue { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public double Confidence { get; set; } = 0.8;
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
