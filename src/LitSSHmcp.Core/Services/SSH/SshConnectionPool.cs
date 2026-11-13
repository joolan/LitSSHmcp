using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LitSSHmcp.Core.Models;
using Renci.SshNet;

namespace LitSSHmcp.Core.Services.SSH;

/// <summary>SSH 连接复用池：命令通道（SshClient）与文件传输通道（SftpClient）分别按服务器复用，空闲自动断开。</summary>
public interface ISshConnectionPool
{
    SshConnectionLease Rent(SshServerConfig server, CancellationToken ct = default);
    SftpConnectionLease RentSftp(SshServerConfig server, CancellationToken ct = default);
}

/// <summary>
/// 连接池满员且在 <see cref="ConnectionPoolConfig.AcquireTimeoutSeconds"/> 内未等到空闲连接时抛出；
/// 避免长命令/长传输占满容量后调用线程无限挂起、取消无法生效。
/// </summary>
public sealed class SshConnectionPoolBusyException : TimeoutException
{
    public SshConnectionPoolBusyException(string message) : base(message) { }
}

/// <summary>
/// 每服务器维护至多 <see cref="ConnectionPoolConfig.MaxPerServer"/> 条连接（命令 / SFTP 各自独立）：执行完**不断开**、放回池；
/// 空闲超过 <see cref="ConnectionPoolConfig.IdleTimeoutSeconds"/> 由后台定时器自动断开。
/// 每条连接用信号量保护（SSH.NET 客户端非线程安全），并发调用分配到不同连接。
/// 配置经 <see cref="Security.ISecurityOptionsProvider"/> 热读取；连接按 host/port/user/认证/密码指纹/(连接数上限) 为键，配置变更自动失效。
/// </summary>
public sealed class SshConnectionPool : ISshConnectionPool, IDisposable
{
    private readonly Pool<SshClient> _commands;
    private readonly Pool<SftpClient> _sftp;
    private readonly Func<ConnectionPoolConfig> _options;
    private readonly Timer _sweeper;

    public SshConnectionPool(
        ISshKnownHostsStore? knownHosts,
        Func<SshHostKeyMode> hostKeyMode,
        Func<ConnectionPoolConfig> options)
        : this(options,
            s => SshClientFactory.Create(s, knownHosts, hostKeyMode()),
            s => SshClientFactory.CreateSftp(s, knownHosts, hostKeyMode()),
            connector: null)
    {
    }

    /// <summary>测试缝：允许注入假工厂与连接动作（connector 为 null 时走真实 Connect）。</summary>
    internal SshConnectionPool(
        Func<ConnectionPoolConfig> options,
        Func<SshServerConfig, SshClient> commandFactory,
        Func<SshServerConfig, SftpClient> sftpFactory,
        Action<BaseClient, ConnectionPoolConfig>? connector)
    {
        _options = options;
        _commands = new Pool<SshClient>(options, commandFactory, connector);
        _sftp = new Pool<SftpClient>(options, sftpFactory, connector);
        _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public SshConnectionLease Rent(SshServerConfig server, CancellationToken ct = default)
    {
        var lease = _commands.Rent(server, ct);
        return new SshConnectionLease((SshClient)lease.Client, lease.Dispose, lease.Invalidate);
    }

    public SftpConnectionLease RentSftp(SshServerConfig server, CancellationToken ct = default)
    {
        var lease = _sftp.Rent(server, ct);
        return new SftpConnectionLease((SftpClient)lease.Client, lease.Dispose);
    }

    private void Sweep()
    {
        var idle = TimeSpan.FromSeconds(Math.Max(0, _options().IdleTimeoutSeconds));
        if (idle <= TimeSpan.Zero)
            return;

        var now = DateTime.UtcNow;
        _commands.SweepIdle(now, idle);
        _sftp.SweepIdle(now, idle);
    }

    public void Dispose()
    {
        _sweeper.Dispose();
        _commands.Dispose();
        _sftp.Dispose();
    }

    // —— 通用池（按客户端类型实例化） ——

    private sealed class Pool<T> : IDisposable where T : BaseClient
    {
        private const int MaxSlotsCap = 16;

        private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly Func<SshServerConfig, T> _factory;
        private readonly Func<ConnectionPoolConfig> _options;
        private readonly Action<BaseClient, ConnectionPoolConfig>? _connector;

        public Pool(Func<ConnectionPoolConfig> options, Func<SshServerConfig, T> factory,
            Action<BaseClient, ConnectionPoolConfig>? connector)
        {
            _options = options;
            _factory = factory;
            _connector = connector;
        }

        public Lease Rent(SshServerConfig server, CancellationToken ct = default)
        {
            var options = _options();
            var max = Math.Clamp(options.MaxPerServer, 1, MaxSlotsCap);
            var entry = _entries.GetOrAdd(Key(server, max), _ => new Entry(_factory, _connector, max));
            return entry.Rent(server, options, ct);
        }

        public void SweepIdle(DateTime now, TimeSpan idle)
        {
            foreach (var kv in _entries)
                kv.Value.SweepIdle(now, idle);
        }

        public void Dispose()
        {
            foreach (var kv in _entries)
                kv.Value.Dispose();
            _entries.Clear();
        }

        public sealed class Lease : IDisposable
        {
            private readonly Action<bool> _release;
            private int _released;

            internal Lease(BaseClient client, Action<bool> release)
            {
                Client = client;
                _release = release;
            }

            public BaseClient Client { get; }

            /// <summary>丢弃当前连接（通道损坏/半开），不是正常归还。</summary>
            public void Invalidate()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                    _release(true);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                    _release(false);
            }
        }

        private sealed class Entry : IDisposable
        {
            private readonly Func<SshServerConfig, T> _factory;
            private readonly Action<BaseClient, ConnectionPoolConfig>? _connector;
            private readonly int _maxPerServer;
            private readonly object _lock = new();
            private readonly List<Slot> _slots = new();
            private readonly SemaphoreSlim _capacity;

            public Entry(Func<SshServerConfig, T> factory, Action<BaseClient, ConnectionPoolConfig>? connector, int maxPerServer)
            {
                _factory = factory;
                _connector = connector;
                _maxPerServer = maxPerServer;
                _capacity = new SemaphoreSlim(maxPerServer, maxPerServer);
            }

            public Lease Rent(SshServerConfig server, ConnectionPoolConfig options, CancellationToken ct)
            {
                // 预留并发容量；满员时有界等待——超时抛"连接池繁忙"而不是无限挂起，ct 取消也能及时生效。
                var acquireTimeout = TimeSpan.FromSeconds(Math.Clamp(options.AcquireTimeoutSeconds, 1, 600));
                if (!_capacity.Wait(acquireTimeout, ct))
                    throw new SshConnectionPoolBusyException(
                        $"连接池繁忙: {server.Host}:{server.Port} 已满 {_maxPerServer} 条连接，" +
                        $"等待 {acquireTimeout.TotalSeconds:0}s 未获得空闲连接（可能有长命令或大文件传输在执行，可稍后重试）");
                Slot slot;
                lock (_lock)
                {
                    slot = _slots.FirstOrDefault(s => s.Gate.Wait(0)) ?? AddSlot();
                }

                try
                {
                    slot.Client = EnsureConnected(slot.Client, server, options);
                    slot.LastUsedUtc = DateTime.UtcNow;
                    return new Lease(slot.Client, discard => Release(slot, discard));
                }
                catch
                {
                    slot.Gate.Release();
                    _capacity.Release();
                    throw;
                }
            }

            private Slot AddSlot()
            {
                var slot = new Slot();
                slot.Gate.Wait();   // 新槽立即可用
                _slots.Add(slot);
                return slot;
            }

            private void Release(Slot slot, bool discard)
            {
                if (discard)
                {
                    // 命令通道可能在 TCP 仍 IsConnected 时已经失效；不能把它放回池复用。
                    // 当前租约持有 slot.Gate，其他 Rent/Sweep 不会同时操作该槽位。
                    SafeDispose(slot.Client);
                    slot.Client = default;
                }
                slot.LastUsedUtc = DateTime.UtcNow;
                slot.Gate.Release();
                _capacity.Release();
            }

            private T EnsureConnected(T? existing, SshServerConfig server, ConnectionPoolConfig options)
            {
                if (existing is { IsConnected: true })
                    return existing;

                SafeDispose(existing);
                var client = _factory(server);
                if (_connector is not null)
                {
                    _connector(client, options);
                    return client;
                }
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.ConnectTimeoutSeconds, 5, 120));
                client.KeepAliveInterval = TimeSpan.FromSeconds(Math.Clamp(options.KeepAliveSeconds, 0, 3600));
                client.Connect();
                return client;
            }

            public void SweepIdle(DateTime now, TimeSpan idle)
            {
                lock (_lock)
                {
                    foreach (var slot in _slots)
                    {
                        if (now - slot.LastUsedUtc < idle)
                            continue;
                        if (!slot.Gate.Wait(0))   // 正在使用，跳过
                            continue;
                        try
                        {
                            if (now - slot.LastUsedUtc >= idle)
                            {
                                SafeDispose(slot.Client);
                                slot.Client = default;
                            }
                        }
                        finally
                        {
                            slot.Gate.Release();
                        }
                    }
                }
            }

            private static void SafeDispose(T? client)
            {
                if (client is null)
                    return;
                try { if (client.IsConnected) client.Disconnect(); } catch { /* ignore */ }
                try { client.Dispose(); } catch { /* ignore */ }
            }

            public void Dispose()
            {
                lock (_lock)
                {
                    foreach (var slot in _slots)
                    {
                        try { slot.Gate.Wait(); } catch { /* ignore */ }
                        SafeDispose(slot.Client);
                        slot.Client = default;
                        try { slot.Gate.Release(); } catch { /* ignore */ }
                        slot.Gate.Dispose();
                    }
                    _slots.Clear();
                }
                _capacity.Dispose();
            }

            private sealed class Slot
            {
                public readonly SemaphoreSlim Gate = new(1, 1);
                public T? Client;
                public DateTime LastUsedUtc = DateTime.UtcNow;
            }
        }

        private static string Key(SshServerConfig s, int max)
        {
            var pw = string.IsNullOrEmpty(s.Password) ? string.Empty : Hash(s.Password!);
            return $"{s.Host}|{s.Port}|{s.Username}|{s.AuthType}|{s.KeyFilePath}|{pw}|{max}|jump={s.ProxyJumpServerId}";
        }

        private static string Hash(string value) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

/// <summary>池化命令连接租约：<see cref="Dispose"/> 时把连接归还池（不断开）。</summary>
public sealed class SshConnectionLease : IDisposable
{
    private readonly Action _release;
    private readonly Action _invalidate;
    private int _released;

    internal SshConnectionLease(SshClient client, Action release, Action invalidate)
    {
        Client = client;
        _release = release;
        _invalidate = invalidate;
    }

    public SshClient Client { get; }

    /// <summary>丢弃当前连接（通道损坏/半开），避免下一次调用继续复用。</summary>
    public void Invalidate()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _invalidate();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _release();
    }
}

/// <summary>池化 SFTP 连接租约：<see cref="Dispose"/> 时把连接归还池（不断开）。</summary>
public sealed class SftpConnectionLease : IDisposable
{
    private readonly Action _release;
    private int _released;

    internal SftpConnectionLease(SftpClient client, Action release)
    {
        Client = client;
        _release = release;
    }

    public SftpClient Client { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _release();
    }
}
