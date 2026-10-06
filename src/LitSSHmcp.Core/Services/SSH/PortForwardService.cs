using System.Collections.Concurrent;
using LitSSHmcp.Core.Models;
using Renci.SshNet;

namespace LitSSHmcp.Core.Services.SSH;

/// <summary>
/// 端口转发服务：为给定的服务器建立本地(-L)/远程(-R)/动态SOCKS(-D)转发，并保持连接直到停止。
/// </summary>
public sealed class PortForwardService : IDisposable
{
    private readonly ConcurrentDictionary<string, Handle> _active = new();
    private readonly ISshKnownHostsStore? _knownHosts;
    private readonly Func<SshHostKeyMode>? _hostKeyMode;

    public PortForwardService(ISshKnownHostsStore? knownHosts = null, Func<SshHostKeyMode>? hostKeyMode = null)
    {
        _knownHosts = knownHosts;
        _hostKeyMode = hostKeyMode;
    }

    public bool IsActive(string id) => _active.ContainsKey(id);

    public IReadOnlyCollection<string> ActiveIds => _active.Keys.ToArray();

    /// <summary>启动一条转发；若已存在则先停止。</summary>
    public async Task<(bool Success, string Message)> StartAsync(SshServerConfig server, PortForwardConfig config)
    {
        await StopAsync(config.Id);

        return await Task.Run(() =>
        {
            SshClient? client = null;
            ForwardedPort? port = null;
            try
            {
                client = SshClientFactory.Create(server, _knownHosts, _hostKeyMode?.Invoke() ?? SshHostKeyMode.Tofu);
                client.Connect();

                switch (config.Type)
                {
                    case PortForwardType.Remote:
                        var remote = new ForwardedPortRemote(BoundHost(config.BindHost), (uint)config.BindPort, config.TargetHost, (uint)config.TargetPort);
                        client.AddForwardedPort(remote);
                        remote.Start();
                        port = remote;
                        break;

                    case PortForwardType.Dynamic:
                        var dynamicPort = new ForwardedPortDynamic(BoundHost(config.BindHost), (uint)config.BindPort);
                        client.AddForwardedPort(dynamicPort);
                        dynamicPort.Start();
                        port = dynamicPort;
                        break;

                    default:
                        var local = new ForwardedPortLocal(BoundHost(config.BindHost), (uint)config.BindPort, config.TargetHost, (uint)config.TargetPort);
                        client.AddForwardedPort(local);
                        local.Start();
                        port = local;
                        break;
                }

                _active[config.Id] = new Handle(client, port);
                return (true, $"已启动：{Describe(config)}");
            }
            catch (Exception ex)
            {
                try { if (port is not null) client?.RemoveForwardedPort(port); } catch { /* ignore */ }
                try { port?.Dispose(); } catch { /* ignore */ }
                try { client?.Dispose(); } catch { /* ignore */ }
                return (false, "启动失败: " + ex.Message);
            }
        });
    }

    private static string BoundHost(string host) => string.IsNullOrWhiteSpace(host) ? "0.0.0.0" : host;

    public Task StopAsync(string id)
    {
        if (_active.TryRemove(id, out var handle))
        {
            try { handle.Port.Stop(); } catch { /* ignore */ }
            try { handle.Client.RemoveForwardedPort(handle.Port); } catch { /* ignore */ }
            try { handle.Port.Dispose(); } catch { /* ignore */ }
            try { handle.Client.Dispose(); } catch { /* ignore */ }
        }
        return Task.CompletedTask;
    }

    public static string Describe(PortForwardConfig c) => c.Type switch
    {
        PortForwardType.Dynamic => $"动态 SOCKS {c.BindHost}:{c.BindPort}",
        PortForwardType.Remote => $"远程 {c.BindPort} -> {c.TargetHost}:{c.TargetPort}",
        _ => $"本地 {c.BindHost}:{c.BindPort} -> {c.TargetHost}:{c.TargetPort}"
    };

    public void Dispose()
    {
        foreach (var id in _active.Keys.ToArray())
            _ = StopAsync(id);
    }

    private sealed record Handle(SshClient Client, ForwardedPort Port);
}
