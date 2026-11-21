using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentFTP;
using FluentFTP.Proxy.AsyncProxy;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.FileTransfer;

public sealed record FtpEntry(
    string Name,
    string FullName,
    bool IsDirectory,
    bool IsSymbolicLink,
    long Size,
    DateTime Modified,
    string Permissions,
    string Owner,
    string Group)
{
    public string Glyph => IsDirectory ? "📁" : "📄";
    public string SizeText => IsDirectory ? string.Empty : FormatSize(Size);
    public string ModifiedText => Modified == default ? string.Empty : Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.#") + " MB";
        return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.#") + " GB";
    }
}

public sealed class FtpConnectionResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public int EntryCount { get; init; }
}

public sealed class FtpFileService
{
    static FtpFileService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<FtpSession> ConnectAsync(
        FileTransferSiteConfig site,
        CancellationToken ct = default)
    {
        site.Normalize();
        var timeoutMs = Math.Max(5, site.TimeoutSeconds) * 1000;
        var (username, password) = Credentials(site);
        var config = new FtpConfig
        {
            EncryptionMode = site.Protocol == FileTransferProtocol.Ftps
                ? site.FtpsMode == FtpsMode.Implicit ? FtpEncryptionMode.Implicit : FtpEncryptionMode.Explicit
                : FtpEncryptionMode.None,
            ValidateAnyCertificate = false,
            ValidateCertificateRevocation = false,
            DataConnectionType = site.Passive ? FtpDataConnectionType.AutoPassive : FtpDataConnectionType.AutoActive,
            SslProtocols = ParseTlsVersion(site.TlsVersion),
            ConnectTimeout = timeoutMs,
            ReadTimeout = timeoutMs,
            DataConnectionConnectTimeout = timeoutMs,
            DataConnectionReadTimeout = timeoutMs,
            NoopInterval = Math.Max(0, site.KeepAliveSeconds),
            SocketKeepAlive = site.KeepAliveSeconds > 0,
            UploadRateLimit = (uint)Math.Max(0, site.SpeedLimitKBps),
            DownloadRateLimit = (uint)Math.Max(0, site.SpeedLimitKBps),
            RetryAttempts = site.AutoReconnect ? 3 : 1,
            SslSessionLength = site.AutoReconnect ? 750 : 0,
            LogToConsole = false
        };
        LoadClientCertificate(site, config);

        var modes = site.Passive
            ? site.PassiveFallback ? new[] { true, false } : new[] { true }
            : new[] { false };
        Exception? lastError = null;
        foreach (var passive in modes)
        {
            var client = CreateClient(site, config, username, password, passive);
            try
            {
                client.Encoding = string.Equals(site.Encoding, "auto", StringComparison.OrdinalIgnoreCase)
                    ? Encoding.UTF8
                    : Encoding.GetEncoding(site.Encoding);
                client.ValidateCertificate += (_, e) => e.Accept = ValidateServerCertificate(site, e);
                await client.Connect(ct).ConfigureAwait(false);
                return new FtpSession(site, client);
            }
            catch (Exception ex)
            {
                lastError = ex;
                client.Dispose();
            }
        }

        throw lastError ?? new IOException("无法建立 FTP 连接。");
    }

    public async Task<FtpConnectionResult> TestConnectionAsync(
        FileTransferSiteConfig site,
        CancellationToken ct = default)
    {
        await using var session = await ConnectAsync(site, ct).ConfigureAwait(false);
        var path = string.IsNullOrWhiteSpace(site.InitialPath) ? "/" : site.InitialPath;
        var items = await session.ListAsync(path, ct).ConfigureAwait(false);
        return new FtpConnectionResult
        {
            Success = true,
            EntryCount = items.Count,
            Message = $"连接成功，当前目录 {items.Count} 项。"
        };
    }

    private static (string Username, string Password) Credentials(FileTransferSiteConfig site)
        => site.AuthType == FileTransferAuthType.Anonymous
            ? (string.IsNullOrWhiteSpace(site.Username) ? "anonymous" : site.Username, "anonymous@")
            : (site.Username, site.Password ?? string.Empty);

    private static AsyncFtpClient CreateClient(
        FileTransferSiteConfig site,
        FtpConfig config,
        string username,
        string password,
        bool passive)
    {
        config.DataConnectionType = passive
            ? FtpDataConnectionType.AutoPassive
            : FtpDataConnectionType.AutoActive;

        if (site.ProxyType == FtpProxyType.None)
            return new AsyncFtpClient(site.Host, username, password, site.Port, config);

        var profile = new FtpProxyProfile
        {
            ProxyHost = site.ProxyHost ?? string.Empty,
            ProxyPort = site.ProxyPort,
            FtpHost = site.Host,
            FtpPort = site.Port,
            FtpCredentials = new NetworkCredential(username, password)
        };
        if (!string.IsNullOrWhiteSpace(site.ProxyUsername))
            profile.ProxyCredentials = new NetworkCredential(site.ProxyUsername, site.ProxyPassword ?? string.Empty);

        AsyncFtpClient client = site.ProxyType switch
        {
            FtpProxyType.Http => new AsyncFtpClientHttp11Proxy(profile),
            FtpProxyType.Socks4 => new AsyncFtpClientSocks4Proxy(profile),
            FtpProxyType.Socks4a => new AsyncFtpClientSocks4aProxy(profile),
            FtpProxyType.Socks5 => new AsyncFtpClientSocks5Proxy(profile),
            _ => new AsyncFtpClient(site.Host, username, password, site.Port, config)
        };
        client.Host = site.Host;
        client.Port = site.Port;
        client.Credentials = new NetworkCredential(username, password);
        client.Config = config;
        return client;
    }

    private static SslProtocols ParseTlsVersion(string? value)
        => (value ?? "auto").ToLowerInvariant() switch
        {
            "tls12" => SslProtocols.Tls12,
            "tls13" => SslProtocols.Tls13,
            "tls12+13" or "tls12,tls13" => SslProtocols.Tls12 | SslProtocols.Tls13,
            _ => SslProtocols.None
        };

    private static void LoadClientCertificate(FileTransferSiteConfig site, FtpConfig config)
    {
        if (string.IsNullOrWhiteSpace(site.ClientCertificatePath))
            return;

        var extension = Path.GetExtension(site.ClientCertificatePath).ToLowerInvariant();
        X509Certificate2 certificate;
        if (extension is ".pfx" or ".p12")
        {
            certificate = new X509Certificate2(
                site.ClientCertificatePath,
                site.ClientCertificatePassword,
                X509KeyStorageFlags.EphemeralKeySet);
        }
        else
        {
            var keyPath = string.IsNullOrWhiteSpace(site.ClientCertificateKeyPath)
                ? site.ClientCertificatePath
                : site.ClientCertificateKeyPath;
            certificate = string.IsNullOrWhiteSpace(site.ClientCertificatePassword)
                ? X509Certificate2.CreateFromPemFile(site.ClientCertificatePath, keyPath)
                : X509Certificate2.CreateFromEncryptedPemFile(
                    site.ClientCertificatePath, site.ClientCertificatePassword, keyPath);
        }

        config.ClientCertificates.Add(certificate);
    }

    private static bool ValidateServerCertificate(FileTransferSiteConfig site, FtpSslValidationEventArgs e)
    {
        if (!site.ValidateCertificate)
            return true;
        if (e.Certificate is null ||
            (e.PolicyErrors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            return false;

        var certificate = e.Certificate as X509Certificate2 ?? new X509Certificate2(e.Certificate);
        if (site.CertificatePins.Length > 0)
        {
            var hash = Convert.ToHexString(SHA256.HashData(certificate.RawData));
            return site.CertificatePins.Any(pin =>
                string.Equals(pin, hash, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(site.CaCertificatePath))
            return ValidateCustomCa(certificate, site.CaCertificatePath);

        return e.PolicyErrors == SslPolicyErrors.None;
    }

    private static bool ValidateCustomCa(X509Certificate2 certificate, string caPath)
    {
        try
        {
            using var ca = new X509Certificate2(caPath);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            return chain.Build(certificate);
        }
        catch
        {
            return false;
        }
    }
}

public sealed class FtpSession : IAsyncDisposable
{
    private readonly AsyncFtpClient _client;
    private bool _disposed;

    internal FtpSession(FileTransferSiteConfig site, AsyncFtpClient client)
    {
        Site = site;
        _client = client;
        CurrentPath = string.IsNullOrWhiteSpace(site.InitialPath) ? "/" : site.InitialPath;
    }

    public FileTransferSiteConfig Site { get; }
    public string CurrentPath { get; set; }
    public bool IsConnected => !_disposed && _client.IsConnected;

    public async Task<IReadOnlyList<FtpEntry>> ListAsync(
        string path,
        CancellationToken ct = default)
    {
        var target = NormalizeRemotePath(path);
        var items = await _client.GetListing(target, ct).ConfigureAwait(false);
        CurrentPath = target;
        return items
            .Where(item => item.Name is not "." and not "..")
            .Select(item => new FtpEntry(
                item.Name,
                NormalizeRemotePath(item.FullName),
                item.Type == FtpObjectType.Directory,
                item.Type == FtpObjectType.Link,
                item.Size,
                item.Modified,
                item.RawPermissions ?? string.Empty,
                item.RawOwner ?? string.Empty,
                item.RawGroup ?? string.Empty))
            .OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task UploadAsync(
        string localPath,
        string remotePath,
        IProgress<double>? progress = null,
        FtpRemoteExists existsMode = FtpRemoteExists.Overwrite,
        CancellationToken ct = default)
    {
        var target = NormalizeRemotePath(remotePath);
        var ftpProgress = progress is null
            ? null
            : new Progress<FtpProgress>(value => progress.Report(Math.Clamp(value.Progress, 0, 100)));
        var status = await _client.UploadFile(
            localPath, target, existsMode, createRemoteDir: true,
            FtpVerify.None, ftpProgress, ct).ConfigureAwait(false);
        if (status == FtpStatus.Failed)
            throw new IOException($"上传失败：{target}");
    }

    public async Task DownloadAsync(
        string remotePath,
        string localPath,
        IProgress<double>? progress = null,
        FtpLocalExists existsMode = FtpLocalExists.Overwrite,
        CancellationToken ct = default)
    {
        var parent = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);
        var source = NormalizeRemotePath(remotePath);
        var ftpProgress = progress is null
            ? null
            : new Progress<FtpProgress>(value => progress.Report(Math.Clamp(value.Progress, 0, 100)));
        var status = await _client.DownloadFile(
            localPath, source, existsMode, FtpVerify.None,
            ftpProgress, ct).ConfigureAwait(false);
        if (status == FtpStatus.Failed)
            throw new IOException($"下载失败：{source}");
    }

    public Task<bool> FileExistsAsync(string path, CancellationToken ct = default)
        => _client.FileExists(NormalizeRemotePath(path), ct);

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default)
        => _client.DirectoryExists(NormalizeRemotePath(path), ct);

    public async Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        var target = NormalizeRemotePath(path);
        await _client.CreateDirectory(target, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default)
    {
        var target = NormalizeRemotePath(path);
        if (isDirectory)
            await _client.DeleteDirectory(target, FtpListOption.Recursive, ct).ConfigureAwait(false);
        else
            await _client.DeleteFile(target, ct).ConfigureAwait(false);
    }

    public async Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
    {
        await _client.Rename(NormalizeRemotePath(oldPath), NormalizeRemotePath(newPath), ct)
            .ConfigureAwait(false);
    }

    public static string NormalizeRemotePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";
        var value = path.Replace('\\', '/');
        if (!value.StartsWith('/'))
            value = "/" + value;
        while (value.Contains("//", StringComparison.Ordinal))
            value = value.Replace("//", "/", StringComparison.Ordinal);
        return value;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (_client.IsConnected)
                await _client.Disconnect().ConfigureAwait(false);
        }
        catch
        {
        }
        _client.Dispose();
    }
}
