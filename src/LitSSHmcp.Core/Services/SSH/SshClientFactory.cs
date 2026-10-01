using Renci.SshNet;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.SSH;

public static class SshClientFactory
{
    public static SshClient Create(
        SshServerConfig server,
        ISshKnownHostsStore? knownHosts = null,
        SshHostKeyMode hostKeyMode = SshHostKeyMode.Tofu)
    {
        if (server.AuthType == AuthType.KeyFile && !string.IsNullOrEmpty(server.KeyFilePath))
        {
            var keyFile = string.IsNullOrEmpty(server.KeyFilePassphrase)
                ? new PrivateKeyFile(server.KeyFilePath)
                : new PrivateKeyFile(server.KeyFilePath, server.KeyFilePassphrase);
            var client = new SshClient(server.Host, server.Port, server.Username, keyFile);
            AttachHostKeyPolicy(client, server, knownHosts, hostKeyMode);
            return client;
        }

        var passwordClient = new SshClient(server.Host, server.Port, server.Username, server.Password ?? string.Empty);
        AttachHostKeyPolicy(passwordClient, server, knownHosts, hostKeyMode);
        return passwordClient;
    }

    public static SftpClient CreateSftp(
        SshServerConfig server,
        ISshKnownHostsStore? knownHosts = null,
        SshHostKeyMode hostKeyMode = SshHostKeyMode.Tofu)
    {
        SftpClient client;
        if (server.AuthType == AuthType.KeyFile && !string.IsNullOrEmpty(server.KeyFilePath))
        {
            var keyFile = string.IsNullOrEmpty(server.KeyFilePassphrase)
                ? new PrivateKeyFile(server.KeyFilePath)
                : new PrivateKeyFile(server.KeyFilePath, server.KeyFilePassphrase);
            client = new SftpClient(server.Host, server.Port, server.Username, keyFile);
        }
        else
        {
            client = new SftpClient(server.Host, server.Port, server.Username, server.Password ?? string.Empty);
        }

        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
        AttachHostKeyPolicy(client, server, knownHosts, hostKeyMode);
        return client;
    }

    /// <summary>
    /// 挂载主机密钥校验（TOFU）：
    /// Tofu —— 首次记录指纹并信任，之后指纹变化即拒绝；
    /// Strict —— 只信任已记录指纹；
    /// Off —— 不校验。
    /// </summary>
    private static void AttachHostKeyPolicy(
        BaseClient client,
        SshServerConfig server,
        ISshKnownHostsStore? knownHosts,
        SshHostKeyMode mode)
    {
        if (mode == SshHostKeyMode.Off || knownHosts == null)
            return;

        client.HostKeyReceived += (_, e) =>
        {
            try
            {
                var presented = e.FingerPrintSHA256;
                if (string.IsNullOrEmpty(presented))
                {
                    e.CanTrust = false;
                    return;
                }

                var existing = knownHosts.Find(server.Host, server.Port);
                if (existing == null)
                {
                    if (mode == SshHostKeyMode.Tofu)
                    {
                        knownHosts.Save(new SshKnownHost
                        {
                            Host = server.Host,
                            Port = server.Port,
                            KeyAlgorithm = e.HostKeyName ?? string.Empty,
                            FingerprintSha256 = presented,
                            FirstSeenUtc = DateTime.UtcNow
                        });
                        e.CanTrust = true;
                    }
                    else
                    {
                        e.CanTrust = false;
                    }

                    return;
                }

                e.CanTrust = string.Equals(existing.FingerprintSha256, presented, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                e.CanTrust = false;
            }
        };
    }
}
