using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using Renci.SshNet;

namespace LitSSHmcp.Core.Services.Jenkins;

/// <summary>
/// 通过已有 SSH 服务器建立本机动态 SOCKS5 转发。Jenkins 请求保留原始主机名，
/// 由 SOCKS5 在 SSH 服务器侧解析，因此 HTTPS SNI 和 Jenkins 返回的绝对 URL 不会被改写。
/// </summary>
public sealed class JenkinsSshTunnel : IDisposable
{
    private readonly SshClient _client;
    private readonly ForwardedPortDynamic _forward;
    private bool _disposed;

    private JenkinsSshTunnel(SshClient client, ForwardedPortDynamic forward)
    {
        _client = client;
        _forward = forward;
    }

    public int LocalPort => (int)_forward.BoundPort;

    public bool IsActive => !_disposed && _client.IsConnected && _forward.IsStarted;

    public static JenkinsSshTunnel Start(
        SshServerConfig server,
        ISshKnownHostsStore? knownHosts = null,
        SshHostKeyMode hostKeyMode = SshHostKeyMode.Tofu)
    {
        ArgumentNullException.ThrowIfNull(server);

        SshClient? client = null;
        ForwardedPortDynamic? forward = null;
        try
        {
            client = SshClientFactory.Create(server, knownHosts, hostKeyMode);
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
            client.KeepAliveInterval = TimeSpan.FromSeconds(15);
            client.Connect();

            forward = new ForwardedPortDynamic("127.0.0.1", 0);
            client.AddForwardedPort(forward);
            forward.Start();

            if (forward.BoundPort == 0)
                throw new InvalidOperationException("SSH 动态转发未分配本地端口。");

            return new JenkinsSshTunnel(client, forward);
        }
        catch
        {
            try { if (forward is not null) client?.RemoveForwardedPort(forward); } catch { /* ignore */ }
            try { forward?.Dispose(); } catch { /* ignore */ }
            try { client?.Dispose(); } catch { /* ignore */ }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { _forward.Stop(); } catch { /* ignore */ }
        try { _client.RemoveForwardedPort(_forward); } catch { /* ignore */ }
        try { _forward.Dispose(); } catch { /* ignore */ }
        try { _client.Dispose(); } catch { /* ignore */ }
    }
}
