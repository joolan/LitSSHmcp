namespace LitSSHmcp.Core.Services.DnsServer;

/// <summary>DNS 配置文档：独立于 config.json，便于单独备份/恢复。</summary>
public sealed class DnsDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DnsOptions Options { get; set; } = new();

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        Options ??= new DnsOptions();
        Options.Normalize();
    }
}
