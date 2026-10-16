using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Datasource;

/// <summary>
/// Redis 会话缓存（装饰器）：同一「数据源 + DB（DefaultDatabase）」复用一条连接与 SSH 隧道，
/// 避免每次命令调用都重建隧道（SSH 隧道模式下 1~3s）并反复消耗数据源限流额度。
/// 数据库工作台与 MCP Redis 工具共用（进程内各持一个实例）。
/// <list type="bullet">
/// <item>限流：建连走 <see cref="IRedisConnectionProvider.OpenSharedAsync"/>（握手后立即归还租约，不长期占用并发额度）。</item>
/// <item>并发：<see cref="RedisClient"/> 非线程安全（共享流无锁），同一会话的命令经信号量串行。</item>
/// <item>失效：命令抛异常（含取消/超时）说明回复可能停在半途、连接流不可信 → 立即丢弃、下次重建。</item>
/// <item>生命周期：无引用后空闲 <see cref="IdleTimeout"/> 自动断开隧道。</item>
/// </list>
/// </summary>
public sealed class RedisSessionCache : IRedisConnectionProvider, IDisposable
{
    /// <summary>最后一个引用释放后的空闲保活时长（之后自动断开连接与隧道）。</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private readonly IRedisConnectionProvider _inner;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private bool _disposed;

    public RedisSessionCache(IRedisConnectionProvider inner)
    {
        _inner = inner;
    }

    public Task<IRedisSession> OpenAsync(DataSourceConfig ds, CancellationToken ct = default)
        => OpenCachedAsync(ds, ct);

    public Task<IRedisSession> OpenSharedAsync(DataSourceConfig ds, CancellationToken ct = default)
        => OpenCachedAsync(ds, ct);

    private async Task<IRedisSession> OpenCachedAsync(DataSourceConfig ds, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = Fingerprint(ds);
        while (true)
        {
            if (_entries.TryGetValue(key, out var existing) && TryAcquire(existing))
                return new CachedSession(this, existing);

            var session = await _inner.OpenSharedAsync(ds, ct);
            var entry = new Entry(key, session);
            if (_entries.TryAdd(key, entry))
            {
                lock (entry.Sync)
                {
                    entry.RefCount = 1;
                }
                return new CachedSession(this, entry);
            }

            // 竞争：其它调用方已先建好，丢弃刚建的连接复用已有的
            try { session.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>占用一个引用；失效的条目顺带从字典摘除。</summary>
    private bool TryAcquire(Entry entry)
    {
        lock (entry.Sync)
        {
            if (entry.Dead)
            {
                _entries.TryRemove(new KeyValuePair<string, Entry>(entry.Key, entry));
                return false;
            }

            entry.IdleTimer?.Dispose();
            entry.IdleTimer = null;
            entry.RefCount++;
            return true;
        }
    }

    private void Release(Entry entry)
    {
        lock (entry.Sync)
        {
            if (entry.Dead || --entry.RefCount > 0)
                return;

            entry.IdleTimer?.Dispose();
            entry.IdleTimer = new Timer(_ => OnIdle(entry), null, IdleTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnIdle(Entry entry)
    {
        lock (entry.Sync)
        {
            if (entry.Dead || entry.RefCount > 0)
                return;
            entry.Dead = true;
        }

        _entries.TryRemove(new KeyValuePair<string, Entry>(entry.Key, entry));
        try { entry.Session.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>命令失败后丢弃会话（连接流可能停在回复半途，不能继续复用）。</summary>
    private void Kill(Entry entry)
    {
        lock (entry.Sync)
        {
            if (entry.Dead)
                return;
            entry.Dead = true;
            entry.IdleTimer?.Dispose();
            entry.IdleTimer = null;
        }

        _entries.TryRemove(new KeyValuePair<string, Entry>(entry.Key, entry));
        try { entry.Session.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>连接配置指纹：配置变更（端口/密码/DB/隧道）后自动走新连接，不复用旧的。</summary>
    private static string Fingerprint(DataSourceConfig ds)
    {
        var raw = string.Join('|', ds.Id, ds.Host, ds.Port, ds.AccessMode, ds.TunnelServerId,
            ds.Username, ds.Password, ds.DefaultDatabase);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var key in _entries.Keys.ToArray())
        {
            if (!_entries.TryRemove(key, out var entry))
                continue;
            lock (entry.Sync)
            {
                entry.Dead = true;
                entry.IdleTimer?.Dispose();
                entry.IdleTimer = null;
            }
            try { entry.Session.Dispose(); } catch { /* ignore */ }
        }
    }

    private sealed class Entry
    {
        public Entry(string key, IRedisSession session)
        {
            Key = key;
            Session = session;
        }

        public string Key { get; }
        public IRedisSession Session { get; }

        /// <summary>命令串行闸（RedisClient 共享流非线程安全）。</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public object Sync { get; } = new();
        public int RefCount;
        public bool Dead;
        public Timer? IdleTimer;
    }

    private sealed class CachedSession : IRedisSession
    {
        private readonly RedisSessionCache _cache;
        private readonly Entry _entry;
        private int _disposed;

        public CachedSession(RedisSessionCache cache, Entry entry)
        {
            _cache = cache;
            _entry = entry;
        }

        public RedisClient Client => _entry.Session.Client;

        public string AccessMode => _entry.Session.AccessMode;

        public string? ViaTunnelServer => _entry.Session.ViaTunnelServer;

        public async Task<RedisValue> ExecuteAsync(IReadOnlyList<string> args, CancellationToken ct = default)
        {
            await _entry.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_entry.Dead)
                    throw new IOException("Redis 连接已失效（超时/中断后已丢弃），请重试。");

                return await _entry.Session.ExecuteAsync(args, ct).ConfigureAwait(false);
            }
            catch
            {
                // 任何失败（含超时/取消）都可能让回复停在半途 → 该连接不可再复用
                _cache.Kill(_entry);
                throw;
            }
            finally
            {
                _entry.Gate.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _cache.Release(_entry);
        }
    }
}
