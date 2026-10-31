using LitSSHmcp.Core.Services.Topology;
using Xunit;

namespace LitSSHmcp.Tests;

public class TopologyIdsTests
{
    [Fact]
    public void NormalizeDomain_lowercases_trims_dot_and_converts_idn()
    {
        Assert.Equal("shop.example.com", TopologyIds.NormalizeDomain("Shop.Example.COM."));
        Assert.Equal("xn--fsqu00a.xn--0zwm56d", TopologyIds.NormalizeDomain("例子.测试."));
        Assert.Equal("*.xn--fsqu00a.xn--0zwm56d", TopologyIds.NormalizeDomain("*.例子.测试."));
    }

    [Fact]
    public void NormalizeHost_canonicalizes_ip_and_hostname()
    {
        Assert.Equal("10.0.0.1", TopologyIds.NormalizeHost("10.0.0.1"));
        Assert.Equal("web-01", TopologyIds.NormalizeHost("WEB-01."));
        Assert.Equal("2001:db8::1", TopologyIds.NormalizeHost("[2001:DB8::1]"));
    }
}
