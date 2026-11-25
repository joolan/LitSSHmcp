using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LitSSHmcp.Core.Services.Proxy;

namespace LitSSHmcp.Core.Services.DnsServer;

/// <summary>
/// 轻量转发型 DNS 宿主：UDP/TCP 监听、客户端 ACL、本地记录、缓存、上游转发和临时到期。
/// 只做转发与缓存，不做递归、权威区、DNSSEC 验证或 DHCP。
/// </summary>
public sealed class DnsServerHost : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly DnsQueryLog _log = new(2000);
    private readonly DnsCache _cache = new();
    private readonly DnsForwarder _forwarder = new();
    private readonly DnsClientRateLimiter _rateLimiter = new();
    private readonly DnsResponseRateLimiter _rrl = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<DnsForwardResult>>> _inflight =
        new(StringComparer.Ordinal);

    private CancellationTokenSource? _cts;
    private Task[] _tasks = Array.Empty<Task>();
    private Socket? _udpSocket;
    private TcpListener? _tcpListener;
    private DnsOptions? _options;
    private DnsLocalResolver _localResolver = new(Array.Empty<DnsLocalRecord>());
    private IReadOnlyList<IPAddress> _selfAddresses = Array.Empty<IPAddress>();
    private string? _udpEndpoint;
    private string? _tcpEndpoint;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _expiresAt;
    private string? _lastError;
    private bool _running;
    private int _actualPort;
    private int _active;
    private long _totalQueries;
    private long _cacheHits;
    private long _forwarded;
    private long _blocked;
    private long _failed;
    private long _coalesced;
    private long _rateLimited;

    public event EventHandler? StateChanged;

    public DnsQueryLog Log => _log;
    public int CacheCount => _cache.Count;

    /// <summary>清空 DNS 缓存；运行中的服务立即生效。</summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>查询某个域名/类型在缓存中的状态，供管理窗口的规则测试使用。</summary>
    public DnsCacheState GetCacheState(string name, ushort type)
    {
        var key = $"{DnsWire.NormalizeName(name)}|{type}|1|0";
        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetFresh(key, now, out _))
            return DnsCacheState.Fresh;
        return _cache.TryGetStale(key, now, out _) ? DnsCacheState.Stale : DnsCacheState.None;
    }

    /// <summary>并发探测当前配置（或系统）上游，返回每个端点的往返耗时。</summary>
    public async Task<IReadOnlyList<DnsUpstreamProbeResult>> ProbeUpstreamsAsync(
        DnsOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Normalize();
        IReadOnlyList<IPAddress> selfAddresses;
        int actualPort;
        lock (_gate)
        {
            selfAddresses = _selfAddresses;
            actualPort = _actualPort;
        }

        var endpoints = DnsForwarder.ResolveUpstreams(options,
            selfAddresses.Count > 0 ? selfAddresses : null, actualPort);
        var timeout = Math.Clamp(options.UpstreamTimeoutMs, 500, 3000);
        var tasks = endpoints
            .Select(endpoint => _forwarder.ProbeAsync(endpoint, timeout, ct))
            .ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public DnsStatus Status
    {
        get
        {
            lock (_gate)
            {
                if (!_running)
                    return DnsStatus.Stopped(_lastError);

                return new DnsStatus(true, _udpEndpoint, _tcpEndpoint,
                    Interlocked.Read(ref _totalQueries),
                    Interlocked.Read(ref _cacheHits),
                    Interlocked.Read(ref _forwarded),
                    Interlocked.Read(ref _blocked),
                    Interlocked.Read(ref _failed),
                    Volatile.Read(ref _active),
                    _startedAt, _expiresAt, _lastError,
                    Interlocked.Read(ref _coalesced),
                    Interlocked.Read(ref _rateLimited));
            }
        }
    }

    public async Task StartAsync(DnsOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Normalize();
        var errors = options.Validate();
        if (errors.Count > 0)
            throw new ArgumentException(string.Join("；", errors));

        await StopAsync().ConfigureAwait(false);

        var bindAddress = ResolveBindAddress(options.BindAddress);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Socket? udp = null;
        TcpListener? tcp = null;
        try
        {
            if (options.TcpEnabled)
            {
                tcp = new TcpListener(bindAddress, options.Port);
                if (bindAddress.AddressFamily == AddressFamily.InterNetworkV6)
                    tcp.Server.DualMode = true;
                tcp.Start(128);
            }

            var port = options.Port;
            if (port == 0 && tcp is not null)
                port = ((IPEndPoint)tcp.LocalEndpoint).Port;

            if (options.UdpEnabled)
            {
                udp = new Socket(bindAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                if (bindAddress.AddressFamily == AddressFamily.InterNetworkV6)
                    udp.DualMode = true;
                udp.Bind(new IPEndPoint(bindAddress, port));
                if (port == 0)
                    port = ((IPEndPoint)udp.LocalEndPoint!).Port;
            }

            if (port == 0)
                throw new InvalidOperationException("无法分配 DNS 监听端口。");

            var startedAt = DateTimeOffset.Now;
            var expiresAt = options.TemporaryEnableMinutes > 0
                ? startedAt.AddMinutes(options.TemporaryEnableMinutes)
                : (DateTimeOffset?)null;
            var applied = options.Clone();
            var selfAddresses = BuildSelfAddresses(bindAddress);
            var udpEndpoint = udp is null
                ? null
                : ((IPEndPoint)udp.LocalEndPoint!).ToString();
            var tcpEndpoint = tcp is null
                ? null
                : ((IPEndPoint)tcp.LocalEndpoint).ToString();

            lock (_gate)
            {
                _options = applied;
                _cts = cts;
                _udpSocket = udp;
                _tcpListener = tcp;
                _udpEndpoint = udpEndpoint;
                _tcpEndpoint = tcpEndpoint;
                _selfAddresses = selfAddresses;
                _actualPort = port;
                _startedAt = startedAt;
                _expiresAt = expiresAt;
                _lastError = null;
                _running = true;
                _localResolver = new DnsLocalResolver(applied.LocalRecords);
                _rateLimiter.Configure(applied.MaxQpsPerClient);
                _rateLimiter.Clear();
                _rrl.Configure(applied.RrlEnabled, applied.RrlResponsesPerSecond,
                    applied.RrlBurst);
                _forwarder.Reset();
                _inflight.Clear();
                _cache.MaxEntries = applied.Cache.MaxEntries;
                _cache.Clear();
                Interlocked.Exchange(ref _totalQueries, 0);
                Interlocked.Exchange(ref _cacheHits, 0);
                Interlocked.Exchange(ref _forwarded, 0);
                Interlocked.Exchange(ref _blocked, 0);
                Interlocked.Exchange(ref _failed, 0);
                Interlocked.Exchange(ref _coalesced, 0);
                Interlocked.Exchange(ref _rateLimited, 0);
                Volatile.Write(ref _active, 0);

                var tasks = new List<Task>();
                if (udp is not null)
                    tasks.Add(UdpLoopAsync(udp, cts.Token));
                if (tcp is not null)
                    tasks.Add(TcpAcceptLoopAsync(tcp, cts.Token));
                _tasks = tasks.ToArray();
            }

            AddSystemLog($"DNS 已启动（UDP {udpEndpoint ?? "关闭"} / TCP {tcpEndpoint ?? "关闭"}）");
            if (expiresAt is { } expiry)
                _ = ExpireAsync(expiry, cts.Token);

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            try { udp?.Dispose(); } catch { /* ignore */ }
            try { tcp?.Stop(); } catch { /* ignore */ }
            cts.Dispose();
            throw new InvalidOperationException(
                $"DNS 监听失败（{bindAddress}:{options.Port}）：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 热更新上游、缓存、本地记录、黑白名单、ACL 与限速等。
    /// 监听地址/端口变化不会重启监听，而是返回 RestartRequired=true。
    /// </summary>
    public DnsRuntimeUpdateResult UpdateRuntime(DnsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Normalize();
        var errors = options.Validate();
        if (errors.Count > 0)
            throw new ArgumentException(string.Join("；", errors));

        bool restartRequired;
        lock (_gate)
        {
            if (!_running || _options is null)
                return new DnsRuntimeUpdateResult(false, false,
                    "DNS 未运行，配置已保存，下次启动生效。");

            restartRequired = ListenerChanged(_options, options);
            var applied = options.Clone();
            if (restartRequired)
            {
                applied.BindAddress = _options.BindAddress;
                applied.Port = _options.Port;
                applied.UdpEnabled = _options.UdpEnabled;
                applied.TcpEnabled = _options.TcpEnabled;
            }

            var cacheChanged = CacheChanged(_options.Cache, applied.Cache);
            _options = applied;
            _localResolver = new DnsLocalResolver(applied.LocalRecords);
            _rateLimiter.Configure(applied.MaxQpsPerClient);
            _rrl.Configure(applied.RrlEnabled, applied.RrlResponsesPerSecond,
                applied.RrlBurst);
            _forwarder.Reset();
            _cache.MaxEntries = applied.Cache.MaxEntries;
            if (cacheChanged || !applied.Cache.Enabled)
                _cache.Clear();
        }

        AddSystemLog(restartRequired
            ? "DNS 配置已热更新；监听地址或端口变化需重启后生效"
            : "DNS 上游/本地记录/黑白名单/缓存策略已热更新");
        StateChanged?.Invoke(this, EventArgs.Empty);

        return new DnsRuntimeUpdateResult(true, restartRequired,
            restartRequired
                ? "配置已热更新；监听地址/端口变化需停止后重新启动 DNS。"
                : "配置已热更新，新查询立即生效。");
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task[] tasks;
        Socket? udp;
        TcpListener? tcp;
        bool wasRunning;

        lock (_gate)
        {
            wasRunning = _running || _cts is not null;
            cts = _cts;
            tasks = _tasks;
            udp = _udpSocket;
            tcp = _tcpListener;
            _cts = null;
            _tasks = Array.Empty<Task>();
            _udpSocket = null;
            _tcpListener = null;
            _running = false;
            _udpEndpoint = null;
            _tcpEndpoint = null;
            _startedAt = null;
            _expiresAt = null;
            _actualPort = 0;
            _selfAddresses = Array.Empty<IPAddress>();
        }

        if (!wasRunning)
            return;

        try { cts?.Cancel(); } catch { /* ignore */ }
        try { udp?.Dispose(); } catch { /* ignore */ }
        try { tcp?.Stop(); } catch { /* ignore */ }

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch { /* 停止过程中的取消/IO 错误忽略 */ }
        cts?.Dispose();
        _cache.Clear();
        _rateLimiter.Clear();
        _rrl.Clear();
        _forwarder.Reset();
        _inflight.Clear();

        AddSystemLog("DNS 已停止");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() =>
        StopAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task UdpLoopAsync(Socket socket, CancellationToken ct)
    {
        var any = socket.AddressFamily == AddressFamily.InterNetworkV6
            ? IPAddress.IPv6Any
            : IPAddress.Any;
        var remote = new IPEndPoint(any, 0);
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, ct)
                    .ConfigureAwait(false);
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
            catch (SocketException)
            {
                continue;
            }

            if (result.ReceivedBytes <= 0 || result.RemoteEndPoint is not IPEndPoint client)
                continue;

            var query = buffer.AsSpan(0, result.ReceivedBytes).ToArray();
            var clientCopy = new IPEndPoint(client.Address, client.Port);
            _ = HandleUdpAsync(socket, query, clientCopy, ct);
        }
    }

    private async Task HandleUdpAsync(Socket socket, byte[] query, IPEndPoint client,
        CancellationToken ct)
    {
        try
        {
            var response = await ProcessQueryAsync(query, client.Address, isTcp: false, ct)
                .ConfigureAwait(false);
            if (response is not null)
                await socket.SendToAsync(response, SocketFlags.None, client, ct)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (ObjectDisposedException)
        {
            // 正常停止
        }
        catch (SocketException)
        {
            // 客户端已关闭 / ICMP 不可达
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }
    }

    private async Task TcpAcceptLoopAsync(TcpListener listener, CancellationToken ct)
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
            catch (SocketException)
            {
                continue;
            }

            _ = HandleTcpClientAsync(client, ct);
        }
    }

    private async Task HandleTcpClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                var lengthBuffer = new byte[2];
                if (!await ReadExactWithTimeoutAsync(stream, lengthBuffer, 30, ct)
                        .ConfigureAwait(false))
                    return;

                var length = (lengthBuffer[0] << 8) | lengthBuffer[1];
                if (length < DnsWire.HeaderLength)
                    return;

                var query = new byte[length];
                if (!await ReadExactWithTimeoutAsync(stream, query, 30, ct).ConfigureAwait(false))
                    return;

                if (client.Client.RemoteEndPoint is not IPEndPoint remote)
                    return;

                var response = await ProcessQueryAsync(query, remote.Address, isTcp: true, ct)
                    .ConfigureAwait(false);
                if (response is null)
                    return;

                var responseLength = new byte[2];
                responseLength[0] = (byte)(response.Length >> 8);
                responseLength[1] = (byte)response.Length;
                await stream.WriteAsync(responseLength, ct).ConfigureAwait(false);
                await stream.WriteAsync(response, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (IOException)
        {
            // 客户端断开
        }
        catch (SocketException)
        {
            // 客户端断开
        }
        catch (ObjectDisposedException)
        {
            // 正常停止
        }
        finally
        {
            try { client.Dispose(); } catch { /* ignore */ }
        }
    }

    private async Task<byte[]?> ProcessQueryAsync(byte[] query, IPAddress clientAddress,
        bool isTcp, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        DnsOptions? options;
        DnsLocalResolver localResolver;
        IReadOnlyList<IPAddress> selfAddresses;
        int actualPort;

        lock (_gate)
        {
            if (!_running || _options is null)
                return null;
            options = _options;
            localResolver = _localResolver;
            selfAddresses = _selfAddresses;
            actualPort = _actualPort;
        }

        if (query.Length < DnsWire.HeaderLength)
            return null;

        if (!DnsWire.TryParseMessage(query, out var layout))
            return DnsWire.BuildError(query, null, 1);

        var question = layout.Question;
        if (layout.IsResponse)
            return null;

        var opcode = (layout.Flags >> 11) & 0x0F;
        if (opcode != 0)
            return DnsWire.BuildError(query, question, 4);

        if (!DnsClientPolicy.IsAllowed(clientAddress, options))
        {
            AddLog(clientAddress, question, "拒绝", "客户端不在白名单", stopwatch.ElapsedMilliseconds);
            return DnsWire.BuildError(query, question, 5);
        }

        if (!_rateLimiter.TryAcquire(clientAddress.ToString()))
        {
            AddLog(clientAddress, question, "拒绝", "超过客户端 QPS 限制", stopwatch.ElapsedMilliseconds);
            return DnsWire.BuildError(query, question, 5);
        }
        _rateLimiter.Trim();
        _rrl.Trim();

        var active = Interlocked.Increment(ref _active);
        if (active > options.MaxInFlight)
        {
            Interlocked.Decrement(ref _active);
            Interlocked.Increment(ref _failed);
            AddLog(clientAddress, question, "失败", "超过在途查询上限", stopwatch.ElapsedMilliseconds);
            return DnsWire.BuildError(query, question, 2);
        }

        try
        {
            Interlocked.Increment(ref _totalQueries);

            // UDP 响应速率限制：超限返回 TC=1，让客户端改走 TCP；TCP 本身不限速。
            byte[] Finish(byte[] response, string result, string upstream)
            {
                if (!isTcp && _rrl.Enabled && !_rrl.TryAcquire(RrlKey(clientAddress, question)))
                {
                    Interlocked.Increment(ref _rateLimited);
                    AddLog(clientAddress, question, "限速", "RRL", stopwatch.ElapsedMilliseconds);
                    return DnsWire.BuildTruncated(query, question);
                }

                AddLog(clientAddress, question, result, upstream, stopwatch.ElapsedMilliseconds);
                return isTcp ? response : FitUdp(response, query, question, options, layout);
            }

            if (question.Class != 1 && question.Class != 255)
            {
                Interlocked.Increment(ref _failed);
                AddLog(clientAddress, question, "拒绝", "不支持的 QCLASS", stopwatch.ElapsedMilliseconds);
                return DnsWire.BuildError(query, question, 5);
            }

            if (options.RefuseAny && question.Type == 255)
            {
                Interlocked.Increment(ref _blocked);
                AddLog(clientAddress, question, "拒绝", "ANY 查询已禁用", stopwatch.ElapsedMilliseconds);
                return DnsWire.BuildError(query, question, 5);
            }

            var local = localResolver.Lookup(question.Name, question.Type);
            if (local.Matched)
            {
                var answers = BuildLocalAnswers(local.Records, question.Type);
                var localResponse = DnsWire.BuildResponse(query, question, answers,
                    rcode: 0, authoritative: true);
                return Finish(localResponse, "本地",
                    answers.Count == 0 ? "本地无该类型记录" : "本地记录");
            }

            var blockReason = DnsDomainPolicy.Evaluate(question.Name,
                options.AllowedDomains, options.BlockedDomains);
            if (blockReason is not null)
            {
                Interlocked.Increment(ref _blocked);
                var blockedResponse = BuildBlockedResponse(query, question, options);
                return Finish(blockedResponse, "拦截", blockReason);
            }

            var cacheKey = CacheKey(question, layout.DnssecOk);
            var cacheable = options.Cache.Enabled && !layout.OptHasOptions;
            DnsCacheEntry? staleEntry = null;
            if (cacheable)
            {
                var now = DateTimeOffset.UtcNow;
                if (_cache.TryGetFresh(cacheKey, now, out var entry))
                {
                    var cachedResponse = (byte[])entry.Response.Clone();
                    DnsWire.SetId(cachedResponse, layout.Id);
                    var elapsed = (uint)Math.Max(0, (now - entry.StoredAt).TotalSeconds);
                    DnsWire.AdjustTtls(cachedResponse, entry.TtlOffsets, elapsed);
                    Interlocked.Increment(ref _cacheHits);
                    return Finish(cachedResponse, "缓存命中", "-");
                }

                // 过期条目只在所有上游都失败后返回，避免 TTL 到期后长期不刷新。
                if (options.Cache.ServeStale && options.Cache.StaleMaxSeconds > 0 &&
                    _cache.TryGetStale(cacheKey, now, out var expired))
                    staleEntry = expired;
            }

            var upstreamQuery = (byte[])query.Clone();
            var upstreamId = (ushort)Random.Shared.Next(1, 65536);
            DnsWire.SetId(upstreamQuery, upstreamId);

            // EDNS 载荷大小会影响上游响应是否被截断，因此按 EDNS 配置分开合并。
            var flightKey = layout.HasOpt
                ? $"{cacheKey}|opt:{layout.UdpPayloadSize}"
                : $"{cacheKey}|no-opt";
            var forward = !layout.OptHasOptions
                ? await ForwardCoalescedAsync(flightKey, upstreamQuery, question, options,
                    selfAddresses, actualPort, ct).ConfigureAwait(false)
                : await _forwarder.ForwardAsync(upstreamQuery, question, options,
                    selfAddresses, actualPort, ct).ConfigureAwait(false);
            if (forward.Response is null)
            {
                if (staleEntry is not null)
                {
                    var cachedResponse = (byte[])staleEntry.Response.Clone();
                    DnsWire.SetId(cachedResponse, layout.Id);
                    DnsWire.AdjustTtls(cachedResponse, staleEntry.TtlOffsets, 0,
                        (uint)options.Cache.StaleTtlSeconds);
                    Interlocked.Increment(ref _cacheHits);
                    return Finish(cachedResponse, "过期缓存", "serve-stale");
                }

                Interlocked.Increment(ref _failed);
                AddLog(clientAddress, question, "失败", "上游无响应",
                    stopwatch.ElapsedMilliseconds);
                return DnsWire.BuildError(query, question, 2);
            }

            var forwarded = (byte[])forward.Response.Clone();
            DnsWire.SetId(forwarded, layout.Id);
            if (!DnsWire.TryParseMessage(forwarded, out var responseLayout))
            {
                Interlocked.Increment(ref _failed);
                AddLog(clientAddress, question, "失败", "上游响应无法解析",
                    stopwatch.ElapsedMilliseconds);
                return DnsWire.BuildError(query, question, 2);
            }

            if (cacheable)
                StoreCache(cacheKey, forwarded, responseLayout, options);

            Interlocked.Increment(ref _forwarded);
            return Finish(forwarded, "转发", forward.Upstream ?? "-");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failed);
            _lastError = ex.Message;
            AddLog(clientAddress, question, "失败", ex.Message,
                stopwatch.ElapsedMilliseconds);
            return DnsWire.BuildError(query, question, 2);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    /// <summary>
    /// 同一域名/类型/DO 位/EDNS 载荷的并发查询只打一次上游，其余调用者复用同一结果。
    /// 仅在请求不含 EDNS 选项时启用，避免不同 EDNS 选项互相影响。
    /// </summary>
    private async Task<DnsForwardResult> ForwardCoalescedAsync(string key, byte[] query,
        DnsQuestion question, DnsOptions options, IReadOnlyList<IPAddress> selfAddresses,
        int actualPort, CancellationToken ct)
    {
        Lazy<Task<DnsForwardResult>>? lazy = null;
        lazy = new Lazy<Task<DnsForwardResult>>(
            () => ForwardAndRemoveAsync(key, lazy!, query, question, options,
                selfAddresses, actualPort, ct),
            LazyThreadSafetyMode.ExecutionAndPublication);

        var existing = _inflight.GetOrAdd(key, lazy);
        if (!ReferenceEquals(existing, lazy))
            Interlocked.Increment(ref _coalesced);

        return await existing.Value.ConfigureAwait(false);
    }

    private async Task<DnsForwardResult> ForwardAndRemoveAsync(string key,
        Lazy<Task<DnsForwardResult>> lazy, byte[] query, DnsQuestion question,
        DnsOptions options, IReadOnlyList<IPAddress> selfAddresses, int actualPort,
        CancellationToken ct)
    {
        try
        {
            return await _forwarder.ForwardAsync(query, question, options,
                selfAddresses, actualPort, ct).ConfigureAwait(false);
        }
        finally
        {
            // 只移除本次飞行任务，避免停止/重启后误删同 key 的新任务。
            ((ICollection<KeyValuePair<string, Lazy<Task<DnsForwardResult>>>>)_inflight)
                .Remove(new KeyValuePair<string, Lazy<Task<DnsForwardResult>>>(key, lazy));
        }
    }

    private static string RrlKey(IPAddress client, DnsQuestion question) =>
        $"{DnsResponseRateLimiter.SubnetKey(client)}|{question.Name}|{question.Type}";

    private void StoreCache(string key, byte[] response, DnsMessageLayout layout,
        DnsOptions options)
    {
        if (layout.Truncated)
            return;
        if (layout.Rcode is not (0 or 3))
            return;

        var negative = layout.Rcode == 3 ||
                       (layout.Rcode == 0 && layout.Answers.Count == 0);
        uint ttl;
        if (negative)
        {
            ttl = DnsWire.GetNegativeTtl(response, layout,
                (uint)Math.Max(0, options.Cache.NegativeTtlSeconds));
            if (ttl == 0)
                return;
            ttl = Math.Clamp(ttl, 1u, (uint)Math.Max(1, options.Cache.MaxTtlSeconds));
        }
        else
        {
            ttl = layout.MinTtl;
            if (ttl == 0)
                return;
            ttl = Math.Clamp(ttl, (uint)options.Cache.MinTtlSeconds,
                (uint)options.Cache.MaxTtlSeconds);
        }

        var now = DateTimeOffset.UtcNow;
        var entry = new DnsCacheEntry
        {
            Response = response,
            TtlOffsets = layout.TtlOffsets.ToArray(),
            StoredAt = now,
            ExpiresAt = now.AddSeconds(ttl),
            StaleUntil = now.AddSeconds(ttl + (uint)options.Cache.StaleMaxSeconds),
            Negative = negative
        };
        _cache.MaxEntries = options.Cache.MaxEntries;
        _cache.Set(key, entry);
    }

    private static byte[] FitUdp(byte[] response, byte[] request, DnsQuestion question,
        DnsOptions options, DnsMessageLayout requestLayout)
    {
        var max = requestLayout.HasOpt
            ? Math.Min(requestLayout.UdpPayloadSize, (ushort)options.MaxUdpResponseBytes)
            : (ushort)512;
        if (max < 512)
            max = 512;
        return response.Length <= max
            ? response
            : DnsWire.BuildTruncated(request, question);
    }

    private static byte[] BuildBlockedResponse(byte[] query, DnsQuestion question,
        DnsOptions options)
    {
        if (options.BlockMode == "zero" && question.Type is 1 or 28)
        {
            var rdata = new byte[question.Type == 1 ? 4 : 16];
            return DnsWire.BuildResponse(query, question,
                new[] { new DnsAnswerRecord(question.Type, 60, rdata) },
                rcode: 0, authoritative: true);
        }
        return DnsWire.BuildError(query, question, 3);
    }

    private static List<DnsAnswerRecord> BuildLocalAnswers(
        IReadOnlyList<DnsLocalRecord> records, ushort queryType)
    {
        var answers = new List<DnsAnswerRecord>();
        foreach (var record in records)
        {
            var type = DnsLocalResolver.TypeToUshort(record.Type);
            if (type == 0)
                continue;
            if (queryType != 255 && queryType != type && type != 5)
                continue;

            byte[] rdata;
            if (type is 1 or 28)
            {
                if (!IPAddress.TryParse(record.Value, out var address))
                    continue;
                rdata = address.GetAddressBytes();
            }
            else
            {
                rdata = DnsWire.EncodeName(record.Value);
            }

            answers.Add(new DnsAnswerRecord(type,
                (uint)Math.Clamp(record.Ttl, 0, 86400), rdata));
        }
        return answers;
    }

    private static string CacheKey(DnsQuestion question, bool dnssecOk) =>
        $"{question.Name}|{question.Type}|{question.Class}|{(dnssecOk ? '1' : '0')}";

    private static bool ListenerChanged(DnsOptions current, DnsOptions next) =>
        !string.Equals(current.BindAddress, next.BindAddress, StringComparison.OrdinalIgnoreCase) ||
        current.Port != next.Port ||
        current.UdpEnabled != next.UdpEnabled ||
        current.TcpEnabled != next.TcpEnabled;

    private static bool CacheChanged(DnsCacheOptions current, DnsCacheOptions next) =>
        current.Enabled != next.Enabled ||
        current.MaxEntries != next.MaxEntries ||
        current.MinTtlSeconds != next.MinTtlSeconds ||
        current.MaxTtlSeconds != next.MaxTtlSeconds ||
        current.NegativeTtlSeconds != next.NegativeTtlSeconds ||
        current.ServeStale != next.ServeStale ||
        current.StaleTtlSeconds != next.StaleTtlSeconds ||
        current.StaleMaxSeconds != next.StaleMaxSeconds;

    private void AddLog(IPAddress client, DnsQuestion question, string result,
        string upstream, long durationMs)
    {
        DnsOptions? options;
        lock (_gate)
            options = _options;
        if (options is { LogQueries: false })
            return;

        _log.Add(new DnsLogEntry(DateTimeOffset.Now, client.ToString(), question.Name,
            TypeText(question.Type), result, upstream,
            (int)Math.Min(int.MaxValue, durationMs)));
    }

    private void AddSystemLog(string message) =>
        _log.Add(new DnsLogEntry(DateTimeOffset.Now, "SYSTEM", "-", "-", message, "-", 0));

    private static string TypeText(ushort type) => type switch
    {
        1 => "A",
        5 => "CNAME",
        6 => "SOA",
        12 => "PTR",
        15 => "MX",
        16 => "TXT",
        28 => "AAAA",
        33 => "SRV",
        41 => "OPT",
        65 => "HTTPS",
        255 => "ANY",
        _ => type.ToString()
    };

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

        _lastError = "临时开启已到期，DNS 已自动停止。";
        await StopAsync().ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static async Task<bool> ReadExactWithTimeoutAsync(NetworkStream stream,
        Memory<byte> buffer, int timeoutSeconds, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer[read..], cts.Token)
                    .ConfigureAwait(false);
                if (count == 0)
                    return false;
                read += count;
            }
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static IReadOnlyList<IPAddress> BuildSelfAddresses(IPAddress bindAddress)
    {
        var normalized = ProxyPolicy.NormalizeAddress(bindAddress);
        if (!normalized.Equals(IPAddress.Any) && !normalized.Equals(IPAddress.IPv6Any))
            return new[] { normalized };

        var addresses = new List<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                    addresses.Add(ProxyPolicy.NormalizeAddress(unicast.Address));
            }
        }
        catch
        {
            // 网卡枚举失败时仍使用回环地址。
        }

        return addresses.Distinct().ToArray();
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
