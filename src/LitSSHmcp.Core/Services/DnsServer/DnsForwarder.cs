using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LitSSHmcp.Core.Services.Proxy;

namespace LitSSHmcp.Core.Services.DnsServer;

public sealed record DnsForwardResult(byte[]? Response, string? Upstream, int Attempts);

/// <summary>
/// 上游转发：UDP 优先，响应被截断时回退 TCP；支持多上游顺序重试和系统 DNS 回退。
/// 默认复用上游 UDP/TCP 连接以降低高并发时的 socket 压力；关闭复用时每次查询使用独立源端口。
/// 事务 ID 始终随机，并对响应的 ID、问题段做校验，降低缓存投毒风险。
/// </summary>
public sealed class DnsForwarder : IDisposable
{
    private readonly ConcurrentDictionary<string, UdpSocketPool> _udpPools =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TcpConnectionPool> _tcpPools =
        new(StringComparer.Ordinal);

    private int _roundRobin;

    /// <summary>丢弃所有空闲的上游连接；在停止/重启/配置热更新时调用。</summary>
    public void Reset()
    {
        foreach (var pool in _udpPools.Values)
            pool.Dispose();
        foreach (var pool in _tcpPools.Values)
            pool.Dispose();
        _udpPools.Clear();
        _tcpPools.Clear();
    }

    public void Dispose() => Reset();

    public async Task<DnsForwardResult> ForwardAsync(byte[] query, DnsQuestion question,
        DnsOptions options, IReadOnlyCollection<IPAddress>? selfAddresses, int selfPort,
        CancellationToken ct = default)
    {
        var endpoints = ResolveUpstreams(options, selfAddresses, selfPort);
        if (endpoints.Count == 0)
            return new DnsForwardResult(null, null, 0);

        var expectedId = DnsWire.ReadU16(query, 0);
        var slot = (uint)Interlocked.Increment(ref _roundRobin) - 1;
        var start = (int)(slot % (uint)endpoints.Count);
        var attempts = 0;

        for (var i = 0; i < endpoints.Count; i++)
        {
            var endpoint = endpoints[(start + i) % endpoints.Count];
            attempts++;

            var response = endpoint.Protocol == "tcp"
                ? await QueryTcpAsync(query, endpoint, options.UpstreamTimeoutMs,
                    options.ReuseUpstreamConnections, expectedId, ct).ConfigureAwait(false)
                : await QueryUdpAsync(query, endpoint, options.UpstreamTimeoutMs,
                    options.ReuseUpstreamConnections, expectedId, ct).ConfigureAwait(false);

            if (response is not null && !DnsWire.IsValidResponse(response, question, expectedId))
                response = null;

            if (response is not null && endpoint.Protocol == "udp" &&
                (DnsWire.ReadU16(response, 2) & 0x0200) != 0)
            {
                var tcpResponse = await QueryTcpAsync(query, endpoint,
                    options.UpstreamTimeoutMs, options.ReuseUpstreamConnections,
                    expectedId, ct).ConfigureAwait(false);
                if (tcpResponse is not null && DnsWire.IsValidResponse(tcpResponse, question, expectedId))
                    response = tcpResponse;
            }

            if (response is not null)
                return new DnsForwardResult(response, endpoint.ToString(), attempts);
        }

        return new DnsForwardResult(null, null, attempts);
    }

    /// <summary>探测单个上游：发送 example.com A 查询，返回往返耗时与响应码。</summary>
    public async Task<DnsUpstreamProbeResult> ProbeAsync(DnsUpstreamEndpoint endpoint,
        int timeoutMs, CancellationToken ct = default)
    {
        var id = (ushort)Random.Shared.Next(1, 65536);
        var query = DnsWire.BuildQuery("example.com", 1, id);
        var stopwatch = Stopwatch.StartNew();
        var response = endpoint.Protocol == "tcp"
            ? await QueryTcpOnceAsync(query, endpoint, timeoutMs, ct).ConfigureAwait(false)
            : await QueryUdpOnceAsync(query, endpoint, timeoutMs, ct).ConfigureAwait(false);
        stopwatch.Stop();

        var latency = (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds);
        if (response is null)
            return new DnsUpstreamProbeResult(endpoint.ToString(), false, latency, -1, "无响应或超时");
        if (!DnsWire.TryParseMessage(response, out var layout))
            return new DnsUpstreamProbeResult(endpoint.ToString(), false, latency, -1, "响应无法解析");
        if (!layout.IsResponse || layout.Id != id)
            return new DnsUpstreamProbeResult(endpoint.ToString(), false, latency, -1, "响应事务 ID 不匹配");

        return new DnsUpstreamProbeResult(endpoint.ToString(), true, latency, layout.Rcode, null);
    }

    public static IReadOnlyList<DnsUpstreamEndpoint> ResolveUpstreams(DnsOptions options,
        IReadOnlyCollection<IPAddress>? selfAddresses, int selfPort)
    {
        var result = new List<DnsUpstreamEndpoint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(DnsUpstreamEndpoint endpoint)
        {
            if (IsSelf(endpoint, selfAddresses, selfPort))
                return;
            if (seen.Add(endpoint.ToString()))
                result.Add(endpoint);
        }

        foreach (var value in options.Upstreams)
        {
            if (DnsUpstreamEndpoint.TryParse(value, out var endpoint))
                Add(endpoint);
        }

        if (result.Count > 0)
            return result;

        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up ||
                    network.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var address in network.GetIPProperties().DnsAddresses)
                {
                    if (address is null)
                        continue;
                    var normalized = ProxyPolicy.NormalizeAddress(address);
                    if (normalized.Equals(IPAddress.Any) || normalized.Equals(IPAddress.IPv6Any))
                        continue;
                    Add(new DnsUpstreamEndpoint("udp", normalized, 53));
                }
            }
        }
        catch
        {
            // 系统 DNS 读取失败时返回空列表，由上层返回 SERVFAIL。
        }

        return result;
    }

    private static bool IsSelf(DnsUpstreamEndpoint endpoint,
        IReadOnlyCollection<IPAddress>? selfAddresses, int selfPort)
    {
        if (selfPort <= 0 || endpoint.Port != selfPort || selfAddresses is null)
            return false;

        var address = ProxyPolicy.NormalizeAddress(endpoint.Address);
        foreach (var self in selfAddresses)
        {
            if (ProxyPolicy.NormalizeAddress(self).Equals(address))
                return true;
        }
        return false;
    }

    private Task<byte[]?> QueryUdpAsync(byte[] query, DnsUpstreamEndpoint endpoint,
        int timeoutMs, bool reuse, ushort expectedId, CancellationToken ct)
    {
        if (!reuse)
            return QueryUdpOnceAsync(query, endpoint, timeoutMs, ct);

        var pool = _udpPools.GetOrAdd(endpoint.ToString(),
            _ => new UdpSocketPool(endpoint));
        return pool.QueryAsync(query, timeoutMs, expectedId, ct);
    }

    private Task<byte[]?> QueryTcpAsync(byte[] query, DnsUpstreamEndpoint endpoint,
        int timeoutMs, bool reuse, ushort expectedId, CancellationToken ct)
    {
        if (!reuse)
            return QueryTcpOnceAsync(query, endpoint, timeoutMs, ct);

        var pool = _tcpPools.GetOrAdd(endpoint.ToString(),
            _ => new TcpConnectionPool(endpoint));
        return pool.QueryAsync(query, timeoutMs, expectedId, ct);
    }

    private static async Task<byte[]?> QueryUdpOnceAsync(byte[] query,
        DnsUpstreamEndpoint endpoint, int timeoutMs, CancellationToken ct)
    {
        using var socket = new Socket(endpoint.Address.AddressFamily, SocketType.Dgram,
            ProtocolType.Udp);
        socket.Connect(new IPEndPoint(endpoint.Address, endpoint.Port));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await socket.SendAsync(query, SocketFlags.None, cts.Token).ConfigureAwait(false);
            var buffer = new byte[4096];
            var received = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token)
                .ConfigureAwait(false);
            return received >= DnsWire.HeaderLength
                ? buffer.AsSpan(0, received).ToArray()
                : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> QueryTcpOnceAsync(byte[] query,
        DnsUpstreamEndpoint endpoint, int timeoutMs, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await client.ConnectAsync(new IPEndPoint(endpoint.Address, endpoint.Port), cts.Token)
                .ConfigureAwait(false);
            var stream = client.GetStream();
            return await ExchangeTcpAsync(stream, query, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ExchangeTcpAsync(NetworkStream stream, byte[] query,
        CancellationToken ct)
    {
        var length = new byte[2];
        length[0] = (byte)(query.Length >> 8);
        length[1] = (byte)query.Length;
        await stream.WriteAsync(length, ct).ConfigureAwait(false);
        await stream.WriteAsync(query, ct).ConfigureAwait(false);

        var lengthBuffer = new byte[2];
        if (!await ReadExactAsync(stream, lengthBuffer, ct).ConfigureAwait(false))
            return null;
        var responseLength = (lengthBuffer[0] << 8) | lengthBuffer[1];
        if (responseLength < DnsWire.HeaderLength)
            return null;

        var response = new byte[responseLength];
        return await ReadExactAsync(stream, response, ct).ConfigureAwait(false)
            ? response
            : null;
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, Memory<byte> buffer,
        CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (count == 0)
                return false;
            read += count;
        }
        return true;
    }

    /// <summary>池内响应只做最小的 ID/QR 校验；完整问题段校验由 ForwardAsync 完成。</summary>
    private static bool LooksValid(byte[] response, ushort expectedId) =>
        response.Length >= DnsWire.HeaderLength &&
        DnsWire.ReadU16(response, 0) == expectedId &&
        (DnsWire.ReadU16(response, 2) & 0x8000) != 0;

    /// <summary>
    /// 复用已连接的 UDP socket；同一 socket 同时只服务一个查询。
    /// 超时或响应异常时直接丢弃 socket，避免迟到响应污染下一次查询。
    /// </summary>
    private sealed class UdpSocketPool : IDisposable
    {
        private const int MaxIdle = 32;
        private readonly IPEndPoint _remote;
        private readonly ConcurrentBag<Socket> _idle = new();
        private int _idleCount;
        private int _disposed;

        public UdpSocketPool(DnsUpstreamEndpoint endpoint)
        {
            _remote = new IPEndPoint(endpoint.Address, endpoint.Port);
        }

        public async Task<byte[]?> QueryAsync(byte[] query, int timeoutMs,
            ushort expectedId, CancellationToken ct)
        {
            Socket? socket = null;
            var returned = false;
            try
            {
                socket = Rent();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                await socket.SendAsync(query, SocketFlags.None, cts.Token).ConfigureAwait(false);

                var buffer = new byte[4096];
                var received = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token)
                    .ConfigureAwait(false);
                if (received < DnsWire.HeaderLength)
                    return null;

                var response = buffer.AsSpan(0, received).ToArray();
                if (!LooksValid(response, expectedId))
                    return null;

                Return(socket);
                returned = true;
                return response;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
            catch (SocketException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            finally
            {
                if (!returned)
                    socket?.Dispose();
            }
        }

        private Socket Rent()
        {
            while (_idle.TryTake(out var socket))
            {
                Interlocked.Decrement(ref _idleCount);
                if (Volatile.Read(ref _disposed) == 0)
                    return socket;
                socket.Dispose();
            }

            var created = new Socket(_remote.AddressFamily, SocketType.Dgram,
                ProtocolType.Udp);
            created.Connect(_remote);
            return created;
        }

        private void Return(Socket socket)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                socket.Dispose();
                return;
            }

            if (Interlocked.Increment(ref _idleCount) > MaxIdle)
            {
                Interlocked.Decrement(ref _idleCount);
                socket.Dispose();
                return;
            }

            _idle.Add(socket);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            while (_idle.TryTake(out var socket))
            {
                Interlocked.Decrement(ref _idleCount);
                socket.Dispose();
            }
        }
    }

    /// <summary>
    /// 复用上游 TCP 连接（一次一个查询）。空闲连接超过 15 秒或连接失效时自动重建；
    /// 连接池为空时按需建立，最多保留 8 条空闲连接。
    /// </summary>
    private sealed class TcpConnectionPool : IDisposable
    {
        private const int MaxIdle = 8;
        private const long IdleTimeoutMs = 15_000;
        private readonly IPEndPoint _remote;
        private readonly ConcurrentBag<Connection> _idle = new();
        private int _idleCount;
        private int _disposed;

        public TcpConnectionPool(DnsUpstreamEndpoint endpoint)
        {
            _remote = new IPEndPoint(endpoint.Address, endpoint.Port);
        }

        public async Task<byte[]?> QueryAsync(byte[] query, int timeoutMs,
            ushort expectedId, CancellationToken ct)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var connection = attempt == 0 ? Rent() : null;
                var reused = connection is not null;
                try
                {
                    connection ??= await ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(timeoutMs);
                    var response = await ExchangeTcpAsync(connection.Stream, query, cts.Token)
                        .ConfigureAwait(false);
                    if (response is null || !LooksValid(response, expectedId))
                    {
                        connection.Dispose();
                        if (reused)
                            continue;
                        return null;
                    }

                    connection.LastUsedMs = Environment.TickCount64;
                    Return(connection);
                    return response;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    connection?.Dispose();
                    return null;
                }
                catch (SocketException)
                {
                    connection?.Dispose();
                    if (!reused)
                        return null;
                }
                catch (IOException)
                {
                    connection?.Dispose();
                    if (!reused)
                        return null;
                }
                catch (ObjectDisposedException)
                {
                    connection?.Dispose();
                    if (!reused)
                        return null;
                }
            }

            return null;
        }

        private Connection? Rent()
        {
            while (_idle.TryTake(out var connection))
            {
                Interlocked.Decrement(ref _idleCount);
                if (Volatile.Read(ref _disposed) == 0 &&
                    Environment.TickCount64 - connection.LastUsedMs <= IdleTimeoutMs)
                    return connection;
                connection.Dispose();
            }
            return null;
        }

        private async Task<Connection> ConnectAsync(int timeoutMs, CancellationToken ct)
        {
            var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try
            {
                await client.ConnectAsync(_remote, cts.Token).ConfigureAwait(false);
                client.NoDelay = true;
                return new Connection
                {
                    Client = client,
                    Stream = client.GetStream(),
                    LastUsedMs = Environment.TickCount64
                };
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private void Return(Connection connection)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                connection.Dispose();
                return;
            }

            if (Interlocked.Increment(ref _idleCount) > MaxIdle)
            {
                Interlocked.Decrement(ref _idleCount);
                connection.Dispose();
                return;
            }

            _idle.Add(connection);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            while (_idle.TryTake(out var connection))
            {
                Interlocked.Decrement(ref _idleCount);
                connection.Dispose();
            }
        }

        private sealed class Connection : IDisposable
        {
            public TcpClient Client = null!;
            public NetworkStream Stream = null!;
            public long LastUsedMs;

            public void Dispose()
            {
                try { Stream?.Dispose(); } catch { /* ignore */ }
                try { Client?.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}
