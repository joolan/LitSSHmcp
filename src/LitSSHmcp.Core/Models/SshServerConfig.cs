namespace LitSSHmcp.Core.Models;

public enum AuthType
{
    Password,
    KeyFile
}

public enum SudoType
{
    None,
    CurrentUser,
    RootUser,
    CustomUser
}

public class SshServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public AuthType AuthType { get; set; } = AuthType.Password;
    public string? Password { get; set; }
    public string? KeyFilePath { get; set; }
    public string? KeyFilePassphrase { get; set; }
    public string? Description { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastConnectedAt { get; set; }

    public SudoType SudoType { get; set; } = SudoType.None;
    public string? SudoUsername { get; set; }
    public string? SudoPassword { get; set; }
}