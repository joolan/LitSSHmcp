using System.Net;
using System.Net.Sockets;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using Renci.SshNet;

namespace LitSSHmcp.Core.Services.Datasource;

/// <summary>
/// 通过 SSH 服务器建立本地端口转发隧道。密码只发送给目标数据库，不落在跳板机上。
/// </summary>
public sealed class SshTunnel : IDisposable
{
    private readonly SshClient _client;
    private readonly ForwardedPortLocal _forwardedPort;

    public uint LocalPort { get; }
    public string ViaServer { get; }

    private SshTunnel(SshClient client, ForwardedPortLocal forwardedPort, uint localPort, string viaServer)
    {
        _client = client;
        _forwardedPort = forwardedPort;
        LocalPort = localPort;
        ViaServer = viaServer;
    }

    public static async Task<SshTunnel> StartAsync(
        SshServerConfig server,
        string remoteHost,
        int remotePort,
        CancellationToken ct = default,
        ISshKnownHostsStore? knownHosts = null,
        SshHostKeyMode hostKeyMode = SshHostKeyMode.Tofu)
    {
        return await Task.Run(() =>
        {
            var client = SshClientFactory.Create(server, knownHosts, hostKeyMode);
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);

            try
            {
                client.Connect();

                var localPort = GetFreePort();
                var forwardedPort = new ForwardedPortLocal(
                    IPAddress.Loopback.ToString(),
                    localPort,
                    remoteHost,
                    (uint)remotePort);

                client.AddForwardedPort(forwardedPort);
                forwardedPort.Start();

                return new SshTunnel(client, forwardedPort, localPort, $"{server.Name}({server.Host})");
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }, ct);
    }

    private static uint GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (uint)((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _forwardedPort.Stop(); } catch { /* ignore */ }
        try { _client.RemoveForwardedPort(_forwardedPort); } catch { /* ignore */ }
        try { _client.Dispose(); } catch { /* ignore */ }
    }
}
