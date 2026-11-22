namespace LitSSHmcp.Core.Services.Proxy;

/// <summary>代理配置文档：独立于 config.json，便于单独备份/恢复。</summary>
public sealed class ProxyDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public ProxyOptions Options { get; set; } = new();
    public List<ProxyAccount> Accounts { get; set; } = new();

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        Options ??= new ProxyOptions();
        Options.Normalize();
        Accounts ??= new List<ProxyAccount>();
        foreach (var account in Accounts)
        {
            account.Id = string.IsNullOrWhiteSpace(account.Id)
                ? Guid.NewGuid().ToString("N")
                : account.Id.Trim();
            account.Username = (account.Username ?? string.Empty).Trim();
            account.Note ??= string.Empty;
            account.PasswordSalt ??= string.Empty;
            account.PasswordHash ??= string.Empty;
            account.Iterations = Math.Max(1000, account.Iterations);
        }
    }
}
