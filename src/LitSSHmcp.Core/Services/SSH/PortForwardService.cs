using System.Collections.Concurrent;
using LitSSHmcp.Core.Models;
using Renci.SshNet;

namespace LitSSHmcp.Core.Services.SSH;

/// <summary>
/// 端口转发服务：为给定的服务器建立本地(-L)/远程(-R)/动态SOCKS(-D)转发，并保持连接直到停止。
/// 特性：① 启动/停止均在后台线程执行（不阻塞 UI），按 id 加锁避免并发启动竞态；
/// ② KeepAlive + ErrorOccurred 监控，意外断开后按"期望状态"指数退避自动重连（2s→30s）。
/// </summary>
public sealed class PortForwardService : IDisposable
{
    private readonly ConcurrentDictionary<string, Handle> _active = new();
    private readonly ConcurrentDictionary<string, (SshServerConfig Server, PortForwardConfig Config)> _desired = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _reconnect = new();
    private readonly ISshKnownHostsStore? _knownHosts;
    private readonly Func<SshHostKeyMode>? _hostKeyMode;
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    /// <summary>状态变化（意外断开/自动重连结果）。在线程池线程触发，订阅方自行调度到 UI 线程。</summary>
    public event Action<string, bool, string>? StateChanged;

    public PortForwardService(ISshKnownHostsStore? knownHosts = null, Func<SshHostKeyMode>? hostKeyMode = null)
    {
        _knownHosts = knownHosts;
        _hostKeyMode = hostKeyMode;
    }

    public bool IsActive(string id) => _active.ContainsKey(id);

    public IReadOnlyCollection<string> ActiveIds => _active.Keys.ToArray();

    private SemaphoreSlim LockFor(string id) => _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    /// <summary>启动一条转发；若已存在则先停止。</summary>
    public async Task<(bool Success, string Message)> StartAsync(SshServerConfig server, PortForwardConfig config)
    {
        var lk = LockFor(config.Id);
        await lk.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(config.Id).ConfigureAwait(false);

            // 先登记"期望运行"：连接建立过程中若被踢断，监控回调才能触发自动重连；失败则撤销。
            _desired[config.Id] = (server, config);
            var result = await Task.Run(() => StartCore(config.Id, server, config)).ConfigureAwait(false);
            if (result.Success)
            {
                CancelReconnect(config.Id);
                // 手动启动也广播，便于看板/多处订阅者同步"运行中"计数
                StateChanged?.Invoke(config.Id, true, result.Message);
            }
            else
            {
                _desired.TryRemove(config.Id, out _);
            }
            return result;
        }
        finally
        {
            lk.Release();
        }
    }

    public Task StopAsync(string id) => Task.Run(async () =>
    {
        var lk = LockFor(id);
        await lk.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(id).ConfigureAwait(false);
            StateChanged?.Invoke(id, false, "已停止");
        }
        finally
        {
            lk.Release();
        }
    });

    /// <summary>移除期望状态并停掉活动句柄；重量级网络操作在后台线程执行。</summary>
    private Task StopCoreAsync(string id)
    {
        _desired.TryRemove(id, out _);
        CancelReconnect(id);

        if (!_active.TryRemove(id, out var handle))
            return Task.CompletedTask;

        return Task.Run(() =>
        {
            try { handle.Port.Stop(); } catch { /* ignore */ }
            try { handle.Client.RemoveForwardedPort(handle.Port); } catch { /* ignore */ }
            try { handle.Port.Dispose(); } catch { /* ignore */ }
            try { handle.Client.Dispose(); } catch { /* ignore */ }
        });
    }

    private (bool Success, string Message) StartCore(string id, SshServerConfig server, PortForwardConfig config)
    {
        SshClient? client = null;
        ForwardedPort? port = null;
        try
        {
            client = SshClientFactory.Create(server, _knownHosts, _hostKeyMode?.Invoke() ?? SshHostKeyMode.Tofu);
            client.KeepAliveInterval = TimeSpan.FromSeconds(15);
            // SSH.NET 无 Disconnected 事件；传输层错误（断网/NAT 超时/服务器重启）经 ErrorOccurred 上抛，
            // KeepAlive 保证空闲断连也能在 15s 内被发现。
            client.ErrorOccurred += (_, e) => OnConnectionLost(id, client, e.Exception?.Message);
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

            _active[id] = new Handle(client, port);
            return (true, $"已启动：{Describe(config)}");
        }
        catch (Exception ex)
        {
            try { if (port is not null) client?.RemoveForwardedPort(port); } catch { /* ignore */ }
            try { port?.Dispose(); } catch { /* ignore */ }
            try { client?.Dispose(); } catch { /* ignore */ }
            return (false, "启动失败: " + ex.Message);
        }
    }

    /// <summary>意外断开：清理句柄并按期望状态调度自动重连。手动停止会先移除句柄，不会走到这里。</summary>
    private void OnConnectionLost(string id, SshClient sender, string? detail = null)
    {
        if (!_active.TryGetValue(id, out var handle) || !ReferenceEquals(handle.Client, sender))
            return;
        if (!_active.TryRemove(id, out _))
            return;

        try { handle.Port.Stop(); } catch { /* ignore */ }
        try { handle.Client.RemoveForwardedPort(handle.Port); } catch { /* ignore */ }
        try { handle.Port.Dispose(); } catch { /* ignore */ }
        try { handle.Client.Dispose(); } catch { /* ignore */ }

        if (!_desired.ContainsKey(id))
        {
            StateChanged?.Invoke(id, false, "连接已断开");
            return;
        }

        StateChanged?.Invoke(id, false, detail is null
            ? "连接断开，自动重连中…"
            : $"连接断开（{detail}），自动重连中…");
        ScheduleReconnect(id);
    }

    private void ScheduleReconnect(string id)
    {
        var cts = new CancellationTokenSource();
        if (!_reconnect.TryAdd(id, cts))
        {
            // 已有重连循环在跑
            return;
        }

        _ = Task.Run(async () =>
        {
            var delayMs = 2000;
            var attempt = 0;
            try
            {
                while (!_shutdown.IsCancellationRequested && !cts.IsCancellationRequested && _desired.ContainsKey(id))
                {
                    try { await Task.Delay(delayMs, cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }

                    if (!_desired.TryGetValue(id, out var want) || _active.ContainsKey(id))
                        break;

                    var lk = LockFor(id);
                    if (!await lk.WaitAsync(0, cts.Token).ConfigureAwait(false))
                    {
                        delayMs = Math.Min(delayMs * 2, 30_000);
                        continue;
                    }

                    (bool Success, string Message) result;
                    try
                    {
                        if (!_desired.ContainsKey(id) || _active.ContainsKey(id))
                            break;
                        attempt++;
                        result = await Task.Run(() => StartCore(id, want.Server, want.Config)).ConfigureAwait(false);
                    }
                    finally
                    {
                        lk.Release();
                    }

                    if (result.Success)
                    {
                        StateChanged?.Invoke(id, true, "已自动重连");
                        break;
                    }

                    StateChanged?.Invoke(id, false, $"自动重连失败（第 {attempt} 次），稍后重试…");
                    delayMs = Math.Min(delayMs * 2, 30_000);
                }
            }
            catch (OperationCanceledException)
            {
                // 停止/退出触发
            }
            finally
            {
                if (_reconnect.TryGetValue(id, out var cur) && ReferenceEquals(cur, cts))
                    _reconnect.TryRemove(id, out _);
            }
        });
    }

    private void CancelReconnect(string id)
    {
        if (_reconnect.TryRemove(id, out var cts))
        {
            try { cts.Cancel(); } catch { /* ignore */ }
        }
    }

    private static string BoundHost(string host) => string.IsNullOrWhiteSpace(host) ? "0.0.0.0" : host;

    public static string Describe(PortForwardConfig c) => c.Type switch
    {
        PortForwardType.Dynamic => $"动态 SOCKS {c.BindHost}:{c.BindPort}",
        PortForwardType.Remote => $"远程 {c.BindPort} -> {c.TargetHost}:{c.TargetPort}",
        _ => $"本地 {c.BindHost}:{c.BindPort} -> {c.TargetHost}:{c.TargetPort}"
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _shutdown.Cancel();
        foreach (var id in _active.Keys.ToArray())
            CancelReconnect(id);

        var tasks = _active.Keys.Select(id => StopAsync(id)).ToArray();
        if (tasks.Length > 0)
        {
            try { Task.WaitAll(tasks, TimeSpan.FromSeconds(3)); } catch { /* ignore */ }
        }

        _shutdown.Dispose();
    }

    private sealed record Handle(SshClient Client, ForwardedPort Port);
}
