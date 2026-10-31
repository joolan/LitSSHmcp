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
    [InlineData("port:s1:tcp-443", "ssh:s1", "hostedOn")]
    [InlineData("app:order", "port:s1:tcp-443", "listensOn")]
    [InlineData("ds:mysql-01", "port:s1:tcp-3306", "listensOn")]
    [InlineData("domain:shop.example.com", "app:order", "exposes")]
    [InlineData("domain:shop.example.com", "cert:abc123", "tlsBy")]
    [InlineData("domain:shop.example.com", "ssh:s1", "resolvesTo")]
    [InlineData("service:s1:nginx", "ssh:s1", "runsOn")]
    [InlineData("container:s1:shop", "ssh:s1", "runsOn")]
    [InlineData("service:s1:nginx", "port:s1:tcp-443", "listensOn")]
    [InlineData("container:s1:shop", "port:s1:tcp-8080", "listensOn")]
    [InlineData("domain:shop.example.com", "service:s1:nginx", "exposes")]
    [InlineData("app:shop", "deploy:shop@1.4.2", "deployedFrom")]
    [InlineData("app:shop", "system:commerce", "partOf")]
    [InlineData("app:shop", "app:gateway", "upstreamOf")]
    [InlineData("app:shop", "app:docker-shop", "runsIn")]
    [InlineData("ds:mysql-01", "app:docker-shop", "runsIn")]
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
    [InlineData("ssh:s1", "port:s1:tcp-443", "hostedOn")]
    [InlineData("app:order", "ssh:s1", "listensOn")]
    [InlineData("app:order", "domain:shop.example.com", "exposes")]
    [InlineData("domain:shop.example.com", "ssh:s1", "tlsBy")]
    [InlineData("container:s1:shop", "app:shop", "runsOn")]
    [InlineData("service:s1:nginx", "app:shop", "runsOn")]
    [InlineData("app:shop", "ssh:s1", "deployedFrom")]
    [InlineData("ssh:s1", "system:commerce", "partOf")]
    [InlineData("ssh:s1", "app:docker-shop", "runsIn")]
    [InlineData("app:shop", "ssh:s1", "runsIn")]
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
