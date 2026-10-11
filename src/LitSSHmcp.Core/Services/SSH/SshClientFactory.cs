using System.Collections.Generic;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Renci.SshNet;

namespace LitSSHmcp.Core.Services.SSH;

/// <summary>
/// 按服务器配置建立 SSH/SFTP 连接。支持 **ProxyJump（跳板机）**：当服务器配置了
/// <see cref="SshServerConfig.ProxyJumpServerId"/> 时，先连跳板机并建立本地端口转发，再经 127.0.0.1:&lt;本地端口&gt;
/// 连目标；目标连接用子类承载"跳板连接 + 转发"，其 <c>Dispose</c> 时一并释放跳板（生命周期自动绑定）。
/// 返回类型仍是 <see cref="SshClient"/> / <see cref="SftpClient"/>，故调用方无需改动。
/// </summary>
public static class SshClientFactory
{
    public static SshClient Create(
        SshServerConfig server,
        ISshKnownHostsStore? knownHosts = null,
        SshHostKeyMode hostKeyMode = SshHostKeyMode.Tofu)
    {
        var (host, port, jump) = ResolveEndpoint(server, knownHosts, hostKeyMode);
        SshClient client = jump is null
            ? new SshClient(BuildConnectionInfo(server, host, port))
            : new ProxiedSshClient(BuildConnectionInfo(server, host, port), jump);

        // 主机密钥（TOFU）始终以"真实目标 server（真实 host/port）"为键；指纹是端到端目标的，故仍正确。
        AttachHostKeyPolicy(client, server, knownHosts, hostKeyMode);
        return client;
    }

    public static SftpClient CreateSftp(
        SshServerConfig server,
        ISshKnownHostsStore? knownHosts = null,
        SshHostKeyMode hostKeyMode = SshHostKeyMode.Tofu)
    {
        var (host, port, jump) = ResolveEndpoint(server, knownHosts, hostKeyMode);
        SftpClient client = jump is null
            ? new SftpClient(BuildConnectionInfo(server, host, port))
            : new ProxiedSftpClient(BuildConnectionInfo(server, host, port), jump);

        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
        AttachHostKeyPolicy(client, server, knownHosts, hostKeyMode);
        return client;
    }

    // ---- 连接信息 / 跳板 ----

    private static ConnectionInfo BuildConnectionInfo(SshServerConfig server, string host, int port)
    {
        AuthenticationMethod method;
        if (server.AuthType == AuthType.KeyFile && !string.IsNullOrEmpty(server.KeyFilePath))
        {
            var keyFile = string.IsNullOrEmpty(server.KeyFilePassphrase)
                ? new PrivateKeyFile(server.KeyFilePath)
                : new PrivateKeyFile(server.KeyFilePath, server.KeyFilePassphrase);
            method = new PrivateKeyAuthenticationMethod(server.Username, keyFile);
        }
        else
        {
            method = new PasswordAuthenticationMethod(server.Username, server.Password ?? string.Empty);
        }

        return new ConnectionInfo(host, port, server.Username, method);
    }

    /// <summary>解析实际连接端点：无跳板→(目标 host/port, null)；有跳板→(127.0.0.1, 本地端口, 跳板隧道)。</summary>
    private static (string Host, int Port, JumpTunnel? Jump) ResolveEndpoint(
        SshServerConfig server, ISshKnownHostsStore? knownHosts, SshHostKeyMode hostKeyMode)
    {
        if (string.IsNullOrWhiteSpace(server.ProxyJumpServerId))
            return (server.Host, server.Port, null);

        var jumpServer = ServerConfigCache.Find(server.ProxyJumpServerId!)
            ?? throw new InvalidOperationException(
                $"经跳板机连接失败：找不到跳板机（Id={server.ProxyJumpServerId}）；请检查配置。");
        if (string.Equals(jumpServer.Id, server.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("经跳板机连接失败：跳板机不能是服务器自身。");
        if (!string.IsNullOrWhiteSpace(jumpServer.ProxyJumpServerId))
            throw new InvalidOperationException("经跳板机连接失败：暂不支持多级跳板（跳板机自身也配置了跳板机）。");

        var jumpClient = new SshClient(BuildConnectionInfo(jumpServer, jumpServer.Host, jumpServer.Port));
        jumpClient.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
        AttachHostKeyPolicy(jumpClient, jumpServer, knownHosts, hostKeyMode);

        try
        {
            jumpClient.Connect();
            var forward = new ForwardedPortLocal("127.0.0.1", 0, server.Host, (uint)Math.Max(1, server.Port));
            jumpClient.AddForwardedPort(forward);
            forward.Start();
            var tunnel = new JumpTunnel(jumpClient, forward);
            return ("127.0.0.1", (int)tunnel.LocalPort, tunnel);
        }
        catch
        {
            try { jumpClient.Dispose(); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>跳板隧道句柄：持有跳板连接与本地转发；由目标客户端 Dispose 时释放。</summary>
    private sealed class JumpTunnel : IDisposable
    {
        private readonly SshClient _client;
        private readonly ForwardedPortLocal _forward;

        public JumpTunnel(SshClient client, ForwardedPortLocal forward)
        {
            _client = client;
            _forward = forward;
        }

        public uint LocalPort => _forward.BoundPort;

        public void Dispose()
        {
            try { _forward.Stop(); } catch { /* ignore */ }
            try { _client.RemoveForwardedPort(_forward); } catch { /* ignore */ }
            try { _client.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>经跳板连接的命令客户端：生命周期内持有跳板隧道，Dispose 时一并释放。</summary>
    private sealed class ProxiedSshClient : SshClient
    {
        private readonly IDisposable _jump;
        public ProxiedSshClient(ConnectionInfo connectionInfo, IDisposable jump) : base(connectionInfo) => _jump = jump;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                try { _jump.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>经跳板连接的 SFTP 客户端：同 <see cref="ProxiedSshClient"/>。</summary>
    private sealed class ProxiedSftpClient : SftpClient
    {
        private readonly IDisposable _jump;
        public ProxiedSftpClient(ConnectionInfo connectionInfo, IDisposable jump) : base(connectionInfo) => _jump = jump;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                try { _jump.Dispose(); } catch { /* ignore */ }
            }
        }
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
