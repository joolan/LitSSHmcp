namespace LitSSHmcp.Core.Services.Security;

public enum RedisCommandKind
{
    /// <summary>只读命令，允许在 redis_read 中直接执行。</summary>
    ReadOnly,
    /// <summary>写/管理命令，必须经 redis_execute 并获得用户桌面审批。</summary>
    Write,
    /// <summary>危险或会让连接挂起的命令，任何情况下都拒绝。</summary>
    Blocked
}

/// <summary>
/// Redis 命令安全策略（第一期为内置固定策略）：
/// - 只读白名单 → redis_read 可直接执行；
/// - 危险命令（清库/关服/换主从/加载模块等）与会让连接阻塞的命令 → 直接拒绝；
/// - 其余一律视为写操作 → 必须用户桌面审批。
/// 后续如需可配置化，可扩展为 security.redisFilter 并接入 SecuritySettingsWindow。
/// </summary>
public static class RedisCommandPolicy
{
    private static readonly HashSet<string> ReadOnlyTopLevel = new(StringComparer.OrdinalIgnoreCase)
    {
        // 连接与探活
        "PING", "ECHO", "INFO", "DBSIZE", "TIME", "LASTSAVE", "ROLE", "QUIT",
        // 键空间（只读）
        "KEYS", "SCAN", "EXISTS", "TYPE", "TTL", "PTTL", "RANDOMKEY", "DUMP",
        // 字符串
        "GET", "MGET", "STRLEN", "GETRANGE", "GETBIT",
        // 哈希
        "HEXISTS", "HGET", "HGETALL", "HKEYS", "HVALS", "HLEN", "HMGET", "HRANDFIELD", "HSCAN",
        // 集合
        "SCARD", "SISMEMBER", "SMISMEMBER", "SMEMBERS", "SRANDMEMBER", "SSCAN",
        "SINTER", "SUNION", "SDIFF",
        // 有序集合
        "ZCARD", "ZCOUNT", "ZRANGE", "ZRANGEBYSCORE", "ZRANGEBYLEX", "ZREVRANGE", "ZREVRANGEBYSCORE",
        "ZREVRANK", "ZRANK", "ZSCORE", "ZMSCORE", "ZSCAN", "ZLEXCOUNT", "ZRANDMEMBER",
        // 列表
        "LLEN", "LRANGE", "LINDEX",
        // 地理位置
        "GEODIST", "GEOPOS", "GEOHASH", "GEOSEARCH",
        // 流（只读）
        "XLEN", "XRANGE", "XREVRANGE", "XPENDING",
        // 其它（不带 STORE/STOREDIST 时无副作用，带了由下方 StoreCapable 判为写）
        "SORT", "GEORADIUS", "GEORADIUSBYMEMBER",
        "ISBUSYKEY"
    };

    /// <summary>带子命令的只读命令族：只有子命令在白名单内才算只读。</summary>
    private static readonly Dictionary<string, HashSet<string>> ReadOnlySubCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CONFIG"] = new(StringComparer.OrdinalIgnoreCase) { "GET", "HELP" },
        ["CLIENT"] = new(StringComparer.OrdinalIgnoreCase) { "LIST", "ID", "INFO" },
        ["ACL"] = new(StringComparer.OrdinalIgnoreCase) { "WHOAMI", "GETUSER", "LIST", "USERS", "CAT", "HELP" },
        ["SLOWLOG"] = new(StringComparer.OrdinalIgnoreCase) { "GET", "LEN", "HELP" },
        ["LATENCY"] = new(StringComparer.OrdinalIgnoreCase) { "HISTORY", "LATEST", "DOCTOR", "HELP" },
        ["MEMORY"] = new(StringComparer.OrdinalIgnoreCase) { "USAGE", "STATS", "DOCTOR", "HELP", "MALLOC-STATS" },
        ["CLUSTER"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "INFO", "SLOTS", "NODES", "SHARDS", "KEYS", "MYID", "COUNTKEYSINSLOT", "GETKEYSINSLOT", "HELP"
        },
        ["COMMAND"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "HELP", "COUNT", "INFO", "DOCS", "LIST", "GETKEYS", "GETKEYSANDFLAGS"
        },
        ["SCRIPT"] = new(StringComparer.OrdinalIgnoreCase) { "EXISTS", "HELP" },
        ["FUNCTION"] = new(StringComparer.OrdinalIgnoreCase) { "LIST", "STATS", "HELP" },
        ["PUBSUB"] = new(StringComparer.OrdinalIgnoreCase) { "CHANNELS", "NUMSUB", "NUMPAT" },
        ["MODULE"] = new(StringComparer.OrdinalIgnoreCase) { "LIST", "HELP" },
        ["OBJECT"] = new(StringComparer.OrdinalIgnoreCase) { "ENCODING", "REFCOUNT", "IDLETIME", "FREQ", "HELP" },
        ["XINFO"] = new(StringComparer.OrdinalIgnoreCase) { "STREAM", "CONSUMERS", "GROUPS", "HELP" }
    };

    private static readonly HashSet<string> BlockedTopLevel = new(StringComparer.OrdinalIgnoreCase)
    {
        // 关服 / 清库 / 调试
        "SHUTDOWN", "FLUSHALL", "FLUSHDB", "DEBUG", "SWAPDB",
        // 主从与拓扑变更
        "MIGRATE", "REPLICAOF", "SLAVEOF", "FAILOVER", "REPLCONF",
        // 会让连接阻塞/接管连接的命令（会使 MCP 调用挂死）
        "SUBSCRIBE", "PSUBSCRIBE", "SSUBSCRIBE", "UNSUBSCRIBE", "PUNSUBSCRIBE", "SUNSUBSCRIBE",
        "MONITOR", "SYNC", "PSYNC",
        "BLPOP", "BRPOP", "BZPOPMIN", "BZPOPMAX", "BLMOVE", "BRPOPLPUSH", "BLMPOP", "BZMPOP",
        "WAIT", "WAITAOF"
    };

    private static readonly Dictionary<string, HashSet<string>> BlockedSubCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MODULE"] = new(StringComparer.OrdinalIgnoreCase) { "LOAD", "UNLOAD" },
        ["ACL"] = new(StringComparer.OrdinalIgnoreCase) { "LOAD", "SAVE" },
        ["CLUSTER"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "MEET", "FORGET", "RESET", "REPLICATE", "FAILOVER",
            "ADDSLOTS", "DELSLOTS", "FLUSHSLOTS", "SETSLOT"
        },
        ["CONFIG"] = new(StringComparer.OrdinalIgnoreCase) { "REWRITE" },
        ["SCRIPT"] = new(StringComparer.OrdinalIgnoreCase) { "KILL" }
    };

    /// <summary>带 STORE 类副作用、必须按写处理的"读"命令。</summary>
    private static readonly HashSet<string> StoreCapable = new(StringComparer.OrdinalIgnoreCase)
    {
        "SORT", "GEORADIUS", "GEORADIUSBYMEMBER"
    };

    public static RedisCommandKind Classify(string? commandLine)
    {
        var tokens = SplitTokens(commandLine);
        if (tokens.Length == 0) return RedisCommandKind.Blocked;

        var name = tokens[0].ToUpperInvariant();

        if (BlockedTopLevel.Contains(name)) return RedisCommandKind.Blocked;

        if (BlockedSubCommands.TryGetValue(name, out var blockedSubs) &&
            tokens.Length > 1 && blockedSubs.Contains(tokens[1].ToUpperInvariant()))
            return RedisCommandKind.Blocked;

        if (StoreCapable.Contains(name) &&
            tokens.Skip(1).Any(t => t.Equals("STORE", StringComparison.OrdinalIgnoreCase) ||
                                    t.Equals("STOREDIST", StringComparison.OrdinalIgnoreCase)))
            return RedisCommandKind.Write;

        // 子命令命令族（CONFIG/CLIENT/ACL/...）: 只有子命令在白名单内才只读;
        // 裸命令(无子命令)只会得到参数错误, 视为只读无害
        if (ReadOnlySubCommands.TryGetValue(name, out var readOnlySubs))
        {
            if (tokens.Length <= 1) return RedisCommandKind.ReadOnly;
            return readOnlySubs.Contains(tokens[1]) ? RedisCommandKind.ReadOnly : RedisCommandKind.Write;
        }

        return ReadOnlyTopLevel.Contains(name) ? RedisCommandKind.ReadOnly : RedisCommandKind.Write;
    }

    public static string CommandName(string? commandLine)
    {
        var tokens = SplitTokens(commandLine);
        return tokens.Length == 0 ? string.Empty : tokens[0].ToUpperInvariant();
    }

    private static string[] SplitTokens(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return Array.Empty<string>();
        return commandLine.Split(new[] { ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
