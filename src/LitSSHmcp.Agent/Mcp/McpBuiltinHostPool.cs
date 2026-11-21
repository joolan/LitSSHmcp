using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Agent;

/// <summary>
/// 内置 MCP 宿主池的键：启动方式 + 会话身份/工作目录 + 配置代际。
/// 工作目录必须进键：MCP 子进程把会话工作目录烘进进程环境，跨目录共享会破坏路径白名单。
/// </summary>
public readonly record struct McpBuiltinHostKey(
    string LaunchFile,
    string LaunchArguments,
    string? WorkingDirectory,
    string OwnerUserId,
    string? SessionWorkspaceDir,
    string? OwnerUsername,
    string? OwnerRole,
    int ConfigGeneration);

/// <summary>内置 MCP 宿主租约；Dispose 只归还池，不直接结束子进程。</summary>
public sealed class McpHostLease : IAsyncDisposable
{
    private readonly Func<ValueTask> _release;
    private int _released;

    internal McpHostLease(McpToolHost host, Func<ValueTask> release)
    {
        Host = host;
        _release = release;
    }

    public McpToolHost Host { get; }

    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _released, 1) == 0 ? _release() : ValueTask.CompletedTask;
}

/// <summary>
/// 进程级内置 MCP 宿主池：相同（启动方式、身份、会话工作目录、配置代际）复用同一个子进程，
/// 避免每次装配 Runtime 都重新 spawn + initialize + tools/list（实测约 1.5s）。
/// 池只缓存内置 MCP；外部 MCP 仍按 runtime 独立连接，保持第三方服务器的隔离语义。
/// </summary>
public static class McpBuiltinHostPool
{
    private static readonly object Gate = new();
    private static readonly Dictionary<McpBuiltinHostKey, Entry> Entries = new();
    private static readonly TimeSpan IdleTtl = TimeSpan.FromMinutes(2);
    private const int MaxIdleEntries = 2;
    private static readonly Timer Sweeper;
    private static int _generation;
    private static bool _shutdown;

    static McpBuiltinHostPool()
    {
        Sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>构造池键（参数与 <see cref="McpToolHost.ForBuiltin"/> 保持一致）。</summary>
    public static McpBuiltinHostKey CreateKey(McpServerLaunch launch, string? ownerUserId,
        string? sessionWorkspaceDir, string? ownerUsername, string? ownerRole, int configGeneration)
    {
        ArgumentNullException.ThrowIfNull(launch);
        return new McpBuiltinHostKey(
            launch.FileName,
            string.Join('\u001f', launch.Arguments),
            launch.WorkingDirectory,
            string.IsNullOrWhiteSpace(ownerUserId) ? "local" : ownerUserId.Trim(),
            NormalizeDir(sessionWorkspaceDir),
            string.IsNullOrWhiteSpace(ownerUsername) ? null : ownerUsername.Trim(),
            string.IsNullOrWhiteSpace(ownerRole) ? null : ownerRole.Trim(),
            Math.Max(0, configGeneration));
    }

    private static string? NormalizeDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            return null;
        try { return Path.GetFullPath(dir.Trim()); }
        catch { return dir.Trim(); }
    }

    /// <summary>获取（必要时创建）一个已连接的内置 MCP 宿主租约。</summary>
    public static async Task<McpHostLease> AcquireAsync(McpBuiltinHostKey key, McpServerLaunch launch,
        CancellationToken ct = default)
    {
        Entry entry;
        Task<McpToolHost> connectTask;
        lock (Gate)
        {
            if (_shutdown)
                throw new ObjectDisposedException(nameof(McpBuiltinHostPool));

            if (key.ConfigGeneration > _generation)
                _generation = key.ConfigGeneration;
            PruneLocked(DateTime.UtcNow);

            if (!Entries.TryGetValue(key, out entry!))
            {
                entry = new Entry(key, launch);
                Entries[key] = entry;
            }

            entry.RefCount++;
            entry.LastUsedUtc = DateTime.UtcNow;
            connectTask = entry.ConnectTask ??= Task.Run(() => ConnectEntryAsync(entry));
        }

        try
        {
            var host = await connectTask.WaitAsync(ct).ConfigureAwait(false);
            return new McpHostLease(host, () => ReleaseAsync(entry));
        }
        catch
        {
            lock (Gate)
            {
                entry.RefCount = Math.Max(0, entry.RefCount - 1);
                entry.ConnectTask = null;
                if (entry.RefCount == 0 &&
                    Entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                {
                    Entries.Remove(key);
                }
            }
            throw;
        }
    }

    /// <summary>后台预热：建立连接后立即归还，空闲宿主保留到 TTL。</summary>
    public static void Prewarm(McpBuiltinHostKey key, McpServerLaunch launch) => _ = Task.Run(async () =>
    {
        try
        {
            await using var _ = await AcquireAsync(key, launch, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 预热失败不影响正式连接（正式路径会重试并上报错误）
        }
    });

    /// <summary>配置代际推进：旧代际的空闲宿主会被释放，使用中的宿主在归还时释放。</summary>
    public static void EnsureGeneration(int generation)
    {
        lock (Gate)
        {
            if (generation > _generation)
                _generation = generation;
            PruneLocked(DateTime.UtcNow);
        }
    }

    /// <summary>诊断用：池内总数 / 使用中 / 空闲 / 连接中。</summary>
    public readonly record struct PoolStats(int Total, int Active, int Idle, int Connecting);

    public static PoolStats GetStats()
    {
        lock (Gate)
        {
            var active = 0;
            var idle = 0;
            var connecting = 0;
            foreach (var entry in Entries.Values)
            {
                if (entry.RefCount > 0)
                    active++;
                else if (entry.Host is null)
                    connecting++;
                else
                    idle++;
            }
            return new PoolStats(Entries.Count, active, idle, connecting);
        }
    }

    /// <summary>App 退出时释放池内全部宿主（含仍在使用的租约，进程即将结束）。</summary>
    public static async Task ShutdownAsync()    {
        List<McpToolHost> hosts = new();
        lock (Gate)
        {
            if (_shutdown)
                return;
            _shutdown = true;
            foreach (var entry in Entries.Values)
            {
                if (entry.Host is not null)
                    hosts.Add(entry.Host);
            }
            Entries.Clear();
            try { Sweeper.Dispose(); } catch { /* 忽略 */ }
        }

        foreach (var host in hosts)
            await DisposeHostAsync(host).ConfigureAwait(false);
    }

    private static async Task<McpToolHost> ConnectEntryAsync(Entry entry)
    {
        var host = McpToolHost.ForBuiltin(entry.Launch, entry.Key.OwnerUserId,
            entry.Key.SessionWorkspaceDir, entry.Key.OwnerUsername, entry.Key.OwnerRole);
        try
        {
            await host.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            entry.Host = host;
            return host;
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static ValueTask ReleaseAsync(Entry entry)
    {
        McpToolHost? dispose = null;
        lock (Gate)
        {
            entry.RefCount = Math.Max(0, entry.RefCount - 1);
            entry.LastUsedUtc = DateTime.UtcNow;
            var stale = _shutdown || entry.Key.ConfigGeneration < _generation;
            if (entry.RefCount == 0 && stale &&
                Entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
            {
                Entries.Remove(entry.Key);
                dispose = entry.Host;
            }
        }

        return dispose is null ? ValueTask.CompletedTask : DisposeHostAsync(dispose);
    }

    private static void Sweep()
    {
        try
        {
            lock (Gate)
                PruneLocked(DateTime.UtcNow);
        }
        catch
        {
            // 清理失败下一轮重试
        }
    }

    /// <summary>清理过期/旧代际/超额的空闲宿主；调用方持锁。</summary>
    private static void PruneLocked(DateTime now)
    {
        var remove = new List<Entry>();
        foreach (var entry in Entries.Values)
        {
            if (entry.RefCount > 0)
                continue;
            if (entry.Key.ConfigGeneration < _generation || now - entry.LastUsedUtc > IdleTtl)
                remove.Add(entry);
        }

        var idle = Entries.Values
            .Where(e => e.RefCount == 0 && !remove.Contains(e))
            .OrderBy(e => e.LastUsedUtc)
            .ToList();
        foreach (var entry in idle.Skip(MaxIdleEntries))
            remove.Add(entry);

        foreach (var entry in remove)
        {
            Entries.Remove(entry.Key);
            if (entry.Host is not null)
                _ = DisposeHostAsync(entry.Host);
        }
    }

    private static async ValueTask DisposeHostAsync(McpToolHost host)
    {
        try { await host.DisposeAsync().ConfigureAwait(false); }
        catch { /* 关停子进程失败忽略 */ }
    }

    private sealed class Entry(McpBuiltinHostKey key, McpServerLaunch launch)
    {
        public McpBuiltinHostKey Key { get; } = key;
        public McpServerLaunch Launch { get; } = launch;
        public Task<McpToolHost>? ConnectTask { get; set; }
        public McpToolHost? Host { get; set; }
        public int RefCount { get; set; }
        public DateTime LastUsedUtc { get; set; }
    }
}
