using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.Tests;

public class RelationRulesTests
{
    [Theory]
    [InlineData("app:order", "ssh:web-01", "runsOn")]
    [InlineData("ds:mysql-01", "ssh:db-01", "runsOn")]
    [InlineData("app:order", "ds:mysql-01", "connectsTo")]
    [InlineData("ssh:web-01", "ds:mysql-01", "canAccess")]
    [InlineData("ssh:web-01", "app:order", "relatedTo")]
    public void Accepts_logical_relations(string from, string to, string type)
    {
        Assert.True(RelationRules.TryValidate(from, to, type, out var error), error);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("ssh:web-01", "ssh:db-01", "runsOn")]   // 服务器 runsOn 服务器：不合逻辑
    [InlineData("app:order", "ds:mysql-01", "runsOn")]  // runsOn 终点必须是服务器
    [InlineData("ssh:web-01", "app:order", "connectsTo")] // connectsTo 必须是 app->ds
    [InlineData("ds:mysql-01", "ssh:db-01", "canAccess")] // canAccess 必须是 ssh->ds
    [InlineData("app:order", "app:other", "canAccess")]
    public void Rejects_illogical_relations(string from, string to, string type)
    {
        Assert.False(RelationRules.TryValidate(from, to, type, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Rejects_self_relation()
    {
        Assert.False(RelationRules.TryValidate("ssh:web-01", "ssh:web-01", "relatedTo", out _));
    }

    [Fact]
    public void Rejects_unknown_type_and_bad_prefix()
    {
        Assert.False(RelationRules.TryValidate("app:a", "ssh:b", "dependsOn", out _));
        Assert.False(RelationRules.TryValidate("order", "web-01", "runsOn", out _));
    }
}
