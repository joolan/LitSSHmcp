using System.Text.Json.Serialization;

namespace LitSSHmcp.Core.Models;

public enum FileTransferProtocol
{
    Ftp,
    Ftps
}

public enum FtpsMode
{
    Explicit,
    Implicit
}

public enum FileTransferAuthType
{
    Password,
    Anonymous
}

public enum FtpProxyType
{
    None,
    Http,
    Socks4,
    Socks4a,
    Socks5
}

public enum FileTransferConflictPolicy
{
    Ask,
    Overwrite,
    Skip,
    Rename,
    Resume
}

public class FileTransferSiteConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public FileTransferProtocol Protocol { get; set; } = FileTransferProtocol.Ftp;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 21;
    public string Username { get; set; } = string.Empty;
    public FileTransferAuthType AuthType { get; set; } = FileTransferAuthType.Password;
    public string? Password { get; set; }
    public FtpsMode FtpsMode { get; set; } = FtpsMode.Explicit;
    public bool ValidateCertificate { get; set; } = true;
    public string TlsVersion { get; set; } = "auto";
    public string? CaCertificatePath { get; set; }
    public string[] CertificatePins { get; set; } = Array.Empty<string>();
    public string? ClientCertificatePath { get; set; }
    public string? ClientCertificateKeyPath { get; set; }
    public string? ClientCertificatePassword { get; set; }
    public bool Passive { get; set; } = true;
    public bool PassiveFallback { get; set; } = true;
    public int KeepAliveSeconds { get; set; } = 30;
    public bool AutoReconnect { get; set; } = true;
    public string Encoding { get; set; } = "utf-8";
    public string InitialPath { get; set; } = "/";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxConcurrentTransfers { get; set; } = 2;
    public int SpeedLimitKBps { get; set; }
    public FileTransferConflictPolicy ConflictPolicy { get; set; } = FileTransferConflictPolicy.Ask;
    public FtpProxyType ProxyType { get; set; } = FtpProxyType.None;
    public string? ProxyHost { get; set; }
    public int ProxyPort { get; set; }
    public string? ProxyUsername { get; set; }
    public string? ProxyPassword { get; set; }
    public string Group { get; set; } = string.Empty;
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string? Description { get; set; }
    public bool Disabled { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastConnectedAt { get; set; }

    [JsonIgnore]
    public string ProtocolText => Protocol == FileTransferProtocol.Ftps
        ? FtpsMode == FtpsMode.Implicit ? "FTPS（隐式）" : "FTPS（显式）"
        : "FTP";

    [JsonIgnore]
    public string AuthText => AuthType == FileTransferAuthType.Anonymous ? "匿名" : "账号密码";

    [JsonIgnore]
    public string TagsText => Tags.Length == 0 ? string.Empty : string.Join(", ", Tags);

    [JsonIgnore]
    public string DisplayName => Disabled ? Name + "（已禁用）" : Name;

    [JsonIgnore]
    public bool IsInsecure => Protocol == FileTransferProtocol.Ftp;

    public FileTransferSiteConfig Clone() => (FileTransferSiteConfig)MemberwiseClone();

    public void Normalize()
    {
        Name = Name?.Trim() ?? string.Empty;
        Host = Host?.Trim() ?? string.Empty;
        Username = Username?.Trim() ?? string.Empty;
        Group = Group?.Trim() ?? string.Empty;
        Description = Description?.Trim();
        ProxyHost = ProxyHost?.Trim();
        ProxyUsername = ProxyUsername?.Trim();
        CaCertificatePath = CaCertificatePath?.Trim();
        ClientCertificatePath = ClientCertificatePath?.Trim();
        ClientCertificateKeyPath = ClientCertificateKeyPath?.Trim();
        Tags = (Tags ?? Array.Empty<string>())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        CertificatePins = (CertificatePins ?? Array.Empty<string>())
            .Select(pin => pin.Replace(":", string.Empty).Replace(" ", string.Empty).Trim().ToUpperInvariant())
            .Where(pin => pin.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        TlsVersion = string.IsNullOrWhiteSpace(TlsVersion) ? "auto" : TlsVersion.Trim().ToLowerInvariant();
        InitialPath = string.IsNullOrWhiteSpace(InitialPath) ? "/" : InitialPath.Trim();
        Encoding = string.IsNullOrWhiteSpace(Encoding) ? "utf-8" : Encoding.Trim().ToLowerInvariant();
        if (Port <= 0)
            Port = Protocol == FileTransferProtocol.Ftps && FtpsMode == FtpsMode.Implicit ? 990 : 21;
        if (TimeoutSeconds <= 0)
            TimeoutSeconds = 30;
        MaxConcurrentTransfers = Math.Clamp(MaxConcurrentTransfers, 1, 8);
        SpeedLimitKBps = Math.Clamp(SpeedLimitKBps, 0, 1024 * 1024);
        KeepAliveSeconds = Math.Clamp(KeepAliveSeconds, 0, 3600);
        if (ProxyType == FtpProxyType.None)
        {
            ProxyHost = null;
            ProxyPort = 0;
            ProxyUsername = null;
            ProxyPassword = null;
        }
        else if (ProxyPort <= 0)
        {
            ProxyPort = ProxyType == FtpProxyType.Http ? 8080 : 1080;
        }
        if (AuthType == FileTransferAuthType.Anonymous)
        {
            Username = string.IsNullOrWhiteSpace(Username) ? "anonymous" : Username;
            Password = null;
        }
    }
}
