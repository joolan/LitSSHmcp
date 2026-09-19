namespace LitSSHmcp.Core.Models;

public class AppConfig
{
    public SshServerConfig[] Servers { get; set; } = Array.Empty<SshServerConfig>();
    public SecurityConfig Security { get; set; } = new();
}

public class SecurityConfig
{
    public CommandFilterConfig CommandFilter { get; set; } = new();
    public FileTransferConfig FileTransfer { get; set; } = new();
}

public class FileTransferConfig
{
    public bool Enabled { get; set; } = true;
    public string[] AllowedLocalPaths { get; set; } = new[] { Environment.GetFolderPath(Environment.SpecialFolder.Desktop) };
    public string[] AllowedRemotePaths { get; set; } = new[] { "/home", "/tmp", "/var/log" };
    public long MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB
    public bool RequireApproval { get; set; } = true;
}