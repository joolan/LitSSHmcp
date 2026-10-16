using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.Tests;

public class RedisCommandPolicyTests
{
    [Theory]
    [InlineData("GET key")]
    [InlineData("HGETALL user:1")]
    [InlineData("INFO")]
    [InlineData("DBSIZE")]
    [InlineData("KEYS user:*")]
    [InlineData("SCAN 0 MATCH a* COUNT 10")]
    [InlineData("SLOWLOG GET 5")]
    [InlineData("CLIENT LIST")]
    [InlineData("CONFIG GET maxmemory")]
    [InlineData("MEMORY USAGE k")]
    [InlineData("CLUSTER INFO")]
    [InlineData("XRANGE events - +")]
    public void Classifies_whitelisted_commands_as_read_only(string command)
    {
        Assert.Equal(RedisCommandKind.ReadOnly, RedisCommandPolicy.Classify(command));
    }

    [Theory]
    [InlineData("SET k v")]
    [InlineData("DEL k")]
    [InlineData("HSET user:1 name tom")]
    [InlineData("EXPIRE k 60")]
    [InlineData("CONFIG SET maxmemory 1gb")]
    [InlineData("CLIENT KILL 123")]
    [InlineData("SLOWLOG RESET")]
    [InlineData("MEMORY PURGE")]
    [InlineData("COMMAND KILL")]
    public void Classifies_writes_as_write(string command)
    {
        Assert.Equal(RedisCommandKind.Write, RedisCommandPolicy.Classify(command));
    }

    [Theory]
    [InlineData("FLUSHALL")]
    [InlineData("FLUSHDB ASYNC")]
    [InlineData("SHUTDOWN NOSAVE")]
    [InlineData("DEBUG SEGFAULT")]
    [InlineData("SWAPDB 0 1")]
    [InlineData("REPLICAOF NO ONE")]
    [InlineData("SUBSCRIBE channel")]
    [InlineData("BLPOP key 0")]
    [InlineData("MONITOR")]
    [InlineData("CONFIG REWRITE")]
    [InlineData("MODULE LOAD /tmp/x.so")]
    [InlineData("ACL SAVE")]
    [InlineData("CLUSTER RESET")]
    public void Classifies_dangerous_commands_as_blocked(string command)
    {
        Assert.Equal(RedisCommandKind.Blocked, RedisCommandPolicy.Classify(command));
    }

    [Fact]
    public void Sort_with_store_is_a_write()
    {
        Assert.Equal(RedisCommandKind.Write, RedisCommandPolicy.Classify("SORT k STORE out"));
        Assert.Equal(RedisCommandKind.ReadOnly, RedisCommandPolicy.Classify("SORT k ALPHA"));
    }

    [Fact]
    public void Config_without_allowed_subcommand_is_a_write()
    {
        Assert.Equal(RedisCommandKind.Write, RedisCommandPolicy.Classify("CONFIG RESETSTAT"));
    }

    [Fact]
    public void Empty_command_is_blocked()
    {
        Assert.Equal(RedisCommandKind.Blocked, RedisCommandPolicy.Classify("   "));
    }

    [Fact]
    public void CommandName_returns_uppercase_first_token()
    {
        Assert.Equal("GET", RedisCommandPolicy.CommandName("get key"));
        Assert.Equal(string.Empty, RedisCommandPolicy.CommandName(null));
    }

    // ---------- IsBulkRisk（工作台执行前的风险提示，不改 Classify 语义） ----------

    [Theory]
    [InlineData("KEYS *")]
    [InlineData("keys user:*")]
    [InlineData("HGETALL big:hash")]
    [InlineData("HKEYS big:hash")]
    [InlineData("HVALS big:hash")]
    [InlineData("SMEMBERS big:set")]
    [InlineData("SORT big:list")]
    [InlineData("SUNION a b")]
    [InlineData("SINTER a b")]
    [InlineData("SDIFF a b")]
    [InlineData("LRANGE big:list 0 -1")]
    [InlineData("LRANGE big:list 0 -100")]
    [InlineData("ZRANGE big:zset 0 -1")]
    [InlineData("ZRANGE big:zset 0 -1 WITHSCORES")]
    [InlineData("ZREVRANGE big:zset 0 -1")]
    [InlineData("ZRANGEBYSCORE big:zset -inf +inf")]
    [InlineData("ZREVRANGEBYSCORE big:zset +inf -inf")]
    [InlineData("XRANGE big:stream - +")]
    [InlineData("XREVRANGE big:stream + -")]
    public void IsBulkRisk_flags_unbounded_reads(string command)
    {
        Assert.True(RedisCommandPolicy.IsBulkRisk(command));
    }

    [Theory]
    [InlineData("GET key")]
    [InlineData("MGET k1 k2")]
    [InlineData("HGET user:1 name")]
    [InlineData("HLEN user:1")]
    [InlineData("LRANGE list:1 0 99")]
    [InlineData("LRANGE list:1 -5 -1")]
    [InlineData("ZRANGE zset:1 -5 -1")]
    [InlineData("ZRANGE zset:1 0 99")]
    [InlineData("ZREVRANGE zset:1 -5 -1 WITHSCORES")]
    [InlineData("ZRANGEBYSCORE zset:1 -inf +inf LIMIT 0 100")]
    [InlineData("ZREVRANGEBYSCORE zset:1 +inf -inf LIMIT 0 100")]
    [InlineData("XRANGE stream:1 - + COUNT 100")]
    [InlineData("XREVRANGE stream:1 + - COUNT 100")]
    [InlineData("SSCAN big:set 0 COUNT 100")]
    [InlineData("HSCAN big:hash 0 COUNT 100")]
    [InlineData("SCAN 0 MATCH a* COUNT 10")]
    [InlineData("STRLEN big:string")]
    [InlineData("GETRANGE big:string 0 1023")]
    [InlineData("TTL key")]
    [InlineData("TYPE key")]
    public void IsBulkRisk_allows_bounded_reads(string command)
    {
        Assert.False(RedisCommandPolicy.IsBulkRisk(command));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsBulkRisk_treats_empty_as_safe(string? command)
    {
        Assert.False(RedisCommandPolicy.IsBulkRisk(command));
    }

    [Fact]
    public void IsBulkRisk_flags_mget_with_many_keys()
    {
        var many = "MGET " + string.Join(' ', Enumerable.Range(0, 80).Select(i => "k" + i));
        var few = "MGET " + string.Join(' ', Enumerable.Range(0, 10).Select(i => "k" + i));
        Assert.True(RedisCommandPolicy.IsBulkRisk(many));
        Assert.False(RedisCommandPolicy.IsBulkRisk(few));
    }

    [Fact]
    public void IsBulkRisk_does_not_change_classify()
    {
        // KEYS / HGETALL 仍是只读（只是提示风险），Classify 语义不变
        Assert.Equal(RedisCommandKind.ReadOnly, RedisCommandPolicy.Classify("KEYS *"));
        Assert.Equal(RedisCommandKind.ReadOnly, RedisCommandPolicy.Classify("HGETALL k"));
        Assert.True(RedisCommandPolicy.IsBulkRisk("KEYS *"));
    }
}
