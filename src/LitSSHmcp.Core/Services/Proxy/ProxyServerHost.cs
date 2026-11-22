using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace LitSSHmcp.Core.Services.Proxy;

/// <summary>
/// 同时托管 HTTP 与 SOCKS5 两个监听器的代理宿主。
/// 两个协议共享账号、客户端白名单、目标策略、并发限制、流量统计和临时到期时间。
/// </summary>
public sealed class ProxyServerHost : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ProxyConnectionLimiter _limiter = new();
    private readonly ProxyRequestLog _log = new(500);
    private readonly List<TcpListener> _listeners = new();
    private readonly ConcurrentDictionary<long, TcpClient> _connections = new();
    private CancellationTokenSource? _cts;
    private Task[] _acceptTasks = Array.Empty<Task>();
    private IReadOnlyList<ProxyAccount> _accounts = Array.Empty<ProxyAccount>();
    private ProxyOptions? _options;
    private string? _httpEndpoint;
    private string? _socks5Endpoint;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _expiresAt;
    private string? _lastError;
    private bool _running;
    private long _connectionSequence;
    private long _totalRequests;
    private long _bytesIn;
    private long _bytesOut;
    private int _active;

    public event EventHandler? StateChanged;

    public ProxyRequestLog Log => _log;

    public IReadOnlyList<ProxyAccount> Accounts
    {
        get { lock (_gate) return _accounts; }
    }

    public ProxyStatus Status
    {
        get
        {
            lock (_gate)
            {
                if (!_running)
                    return ProxyStatus.Stopped(_lastError);
                return new ProxyStatus(true, _httpEndpoint, _socks5Endpoint,
                    Volatile.Read(ref _active),
                    Interlocked.Read(ref _totalRequests),
                    Interlocked.Read(ref _bytesIn),
                    Interlocked.Read(ref _bytesOut),
                    _startedAt, _expiresAt, _lastError);
            }
        }
    }

    public async Task StartAsync(ProxyOptions options, IReadOnlyList<ProxyAccount> accounts,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(accounts);

        options.Normalize();
        var errors = options.Validate();
        if (errors.Count > 0)
            throw new ArgumentException(string.Join("；", errors));

        await StopAsync().ConfigureAwait(false);

        var started = new List<(TcpListener Listener, string Protocol)>();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var bindAddress = ResolveBindAddress(options.BindAddress);
            if (options.HttpEnabled)
                started.Add((StartListener(bindAddress, options.HttpPort), "HTTP"));
            if (options.Socks5Enabled)
                started.Add((StartListener(bindAddress, options.Socks5Port), "SOCKS5"));

            var accountSnapshot = accounts.Select(account => account.Clone()).ToArray();
            var startedAt = DateTimeOffset.Now;
            DateTimeOffset? expiresAt = options.TemporaryEnableMinutes > 0
                ? startedAt.AddMinutes(options.TemporaryEnableMinutes)
                : null;

            lock (_gate)
            {
                _options = options;
                _accounts = accountSnapshot;
                _cts = cts;
                _listeners.Clear();
                _listeners.AddRange(started.Select(item => item.Listener));
                _httpEndpoint = started.FirstOrDefault(item => item.Protocol == "HTTP").Listener?.LocalEndpoint
                    is IPEndPoint http ? http.ToString() : null;
                _socks5Endpoint = started.FirstOrDefault(item => item.Protocol == "SOCKS5").Listener?.LocalEndpoint
                    is IPEndPoint socks ? socks.ToString() : null;
                _startedAt = startedAt;
                _expiresAt = expiresAt;
                _lastError = null;
                _running = true;
                Interlocked.Exchange(ref _totalRequests, 0);
                Interlocked.Exchange(ref _bytesIn, 0);
                Interlocked.Exchange(ref _bytesOut, 0);
                Volatile.Write(ref _active, 0);
                _acceptTasks = started
                    .Select(item => AcceptLoopAsync(item.Listener, item.Protocol, options,
                        accountSnapshot, cts.Token))
                    .ToArray();
            }

            _log.Add(new ProxyLogEntry(DateTimeOffset.Now, "SYSTEM", "-", "-", "-",
                $"代理已启动（HTTP {_httpEndpoint ?? "关闭"} / SOCKS5 {_socks5Endpoint ?? "关闭"}）",
                0, 0));

            if (expiresAt is { } expiry)
                _ = ExpireAsync(expiry, cts.Token);

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            cts.Cancel();
            foreach (var item in started)
            {
                try { item.Listener.Stop(); } catch { /* ignore */ }
            }
            cts.Dispose();
            throw;
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task[] tasks;
        TcpListener[] listeners;
        TcpClient[] connections;
        bool wasRunning;

        lock (_gate)
        {
            wasRunning = _running || _cts is not null;
            cts = _cts;
            tasks = _acceptTasks;
            listeners = _listeners.ToArray();
            connections = _connections.Values.ToArray();
            _cts = null;
            _acceptTasks = Array.Empty<Task>();
            _listeners.Clear();
            _running = false;
            _httpEndpoint = null;
            _socks5Endpoint = null;
            _startedAt = null;
            _expiresAt = null;
        }

        if (!wasRunning)
            return;

        try { cts?.Cancel(); } catch { /* ignore */ }
        foreach (var listener in listeners)
        {
            try { listener.Stop(); } catch { /* ignore */ }
        }
        foreach (var connection in connections)
        {
            try { connection.Dispose(); } catch { /* ignore */ }
        }

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch { /* 停止过程中的取消/IO 错误忽略 */ }
        cts?.Dispose();

        _log.Add(new ProxyLogEntry(DateTimeOffset.Now, "SYSTEM", "-", "-", "-",
            "代理已停止", 0, 0));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() =>
        StopAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task AcceptLoopAsync(TcpListener listener, string protocol,
        ProxyOptions options, IReadOnlyList<ProxyAccount> accounts, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                try { await Task.Delay(100, ct).ConfigureAwait(false); }
                catch { return; }
                continue;
            }

            _ = HandleClientAsync(client, protocol, options, accounts, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, string protocol,
        ProxyOptions options, IReadOnlyList<ProxyAccount> accounts, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _connectionSequence);
        _connections[id] = client;
        var remote = client.Client.RemoteEndPoint;
        var clientKey = ProxyTunnel.ClientKey(remote);
        var clientText = ProxyTunnel.EndpointText(remote);

        if (!_limiter.TryAcquire(clientKey, options.MaxTotalConnections,
                options.MaxConnectionsPerClient, out var lease, out var reason))
        {
            _log.Add(new ProxyLogEntry(DateTimeOffset.Now, protocol, clientText, "-", "-",
                "并发限制: " + reason, 0, 0));
            try { client.Dispose(); } catch { /* ignore */ }
            _connections.TryRemove(id, out _);
            return;
        }

        Interlocked.Increment(ref _active);
        try
        {
            var runtime = new ProxyRuntime(options, accounts, _log,
                (bytesIn, bytesOut) =>
                {
                    Interlocked.Add(ref _bytesIn, bytesIn);
                    Interlocked.Add(ref _bytesOut, bytesOut);
                },
                () => Interlocked.Increment(ref _totalRequests));

            if (protocol == "HTTP")
                await HttpProxyConnection.HandleAsync(client, runtime, ct).ConfigureAwait(false);
            else
                await Socks5ProxyConnection.HandleAsync(client, runtime, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常停止/超时
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }
        finally
        {
            lease?.Dispose();
            Interlocked.Decrement(ref _active);
            _connections.TryRemove(id, out _);
            try { client.Dispose(); } catch { /* ignore */ }
        }
    }

    private async Task ExpireAsync(DateTimeOffset expires, CancellationToken ct)
    {
        var delay = expires - DateTimeOffset.Now;
        if (delay > TimeSpan.Zero)
        {
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch { return; }
        }

        if (ct.IsCancellationRequested)
            return;

        _lastError = "临时开启已到期，代理已自动停止。";
        await StopAsync().ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static TcpListener StartListener(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port);
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            listener.Server.DualMode = true;
        listener.Start(128);
        return listener;
    }

    private static IPAddress ResolveBindAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "*" or "0.0.0.0")
            return IPAddress.Any;
        if (value is "::" or "[::]")
            return IPAddress.IPv6Any;
        if (string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;
        return IPAddress.TryParse(value, out var address)
            ? address
            : throw new ArgumentException("监听地址无效：" + value);
    }
}
