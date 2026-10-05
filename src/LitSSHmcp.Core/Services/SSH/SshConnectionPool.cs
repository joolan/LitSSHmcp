using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LitSSHmcp.Core.Models;
using Renci.SshNet;

namespace LitSSHmcp.Core.Services.SSH;

/// <summary>SSH 连接复用池：命令通道（SshClient）与文件传输通道（SftpClient）分别按服务器复用，空闲自动断开。</summary>
public interface ISshConnectionPool
{
    SshConnectionLease Rent(SshServerConfig server);
    SftpConnectionLease RentSftp(SshServerConfig server);
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
    {
        _options = options;
        _commands = new Pool<SshClient>(options, s => SshClientFactory.Create(s, knownHosts, hostKeyMode()));
        _sftp = new Pool<SftpClient>(options, s => SshClientFactory.CreateSftp(s, knownHosts, hostKeyMode()));
        _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public SshConnectionLease Rent(SshServerConfig server)
    {
        var lease = _commands.Rent(server);
        return new SshConnectionLease((SshClient)lease.Client, lease.Dispose);
    }

    public SftpConnectionLease RentSftp(SshServerConfig server)
    {
        var lease = _sftp.Rent(server);
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

        public Pool(Func<ConnectionPoolConfig> options, Func<SshServerConfig, T> factory)
        {
            _options = options;
            _factory = factory;
        }

        public Lease Rent(SshServerConfig server)
        {
            var options = _options();
            var max = Math.Clamp(options.MaxPerServer, 1, MaxSlotsCap);
            var entry = _entries.GetOrAdd(Key(server, max), _ => new Entry(_factory, max));
            return entry.Rent(server, options);
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
            private readonly Action _release;
            private int _released;

            internal Lease(BaseClient client, Action release)
            {
                Client = client;
                _release = release;
            }

            public BaseClient Client { get; }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                    _release();
            }
        }

        private sealed class Entry : IDisposable
        {
            private readonly Func<SshServerConfig, T> _factory;
            private readonly object _lock = new();
            private readonly List<Slot> _slots = new();
            private readonly SemaphoreSlim _capacity;

            public Entry(Func<SshServerConfig, T> factory, int maxPerServer)
            {
                _factory = factory;
                _capacity = new SemaphoreSlim(maxPerServer, maxPerServer);
            }

            public Lease Rent(SshServerConfig server, ConnectionPoolConfig options)
            {
                _capacity.Wait();   // 预留并发容量；满则等待某条连接归还
                Slot slot;
                lock (_lock)
                {
                    slot = _slots.FirstOrDefault(s => s.Gate.Wait(0)) ?? AddSlot();
                }

                try
                {
                    slot.Client = EnsureConnected(slot.Client, server, options);
                    slot.LastUsedUtc = DateTime.UtcNow;
                    return new Lease(slot.Client, () => Release(slot));
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

            private void Release(Slot slot)
            {
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
            return $"{s.Host}|{s.Port}|{s.Username}|{s.AuthType}|{s.KeyFilePath}|{pw}|{max}";
        }

        private static string Hash(string value) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

/// <summary>池化命令连接租约：<see cref="Dispose"/> 时把连接归还池（不断开）。</summary>
public sealed class SshConnectionLease : IDisposable
{
    private readonly Action _release;
    private int _released;

    internal SshConnectionLease(SshClient client, Action release)
    {
        Client = client;
        _release = release;
    }

    public SshClient Client { get; }

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
