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
}
