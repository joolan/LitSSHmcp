using System.Net;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Web;
using Microsoft.AspNetCore.HttpOverrides;
using Xunit;

namespace LitSSHmcp.Web.Tests;

public class WebForwardedHeadersTests
{
    [Fact]
    public void Parse_trusted_proxies_ignores_blank_invalid_and_duplicates()
    {
        var proxies = WebForwardedHeaders.ParseTrustedProxies(
            new[] { "127.0.0.1", " 10.0.0.5 ", "not-an-ip", "", "127.0.0.1" });

        Assert.Equal(2, proxies.Count);
        Assert.Contains(IPAddress.Loopback, proxies);
        Assert.Contains(IPAddress.Parse("10.0.0.5"), proxies);
    }

    [Fact]
    public void Create_options_is_disabled_without_explicit_proxies()
    {
        Assert.Null(WebForwardedHeaders.CreateOptions(new WebHostConfig()));
        Assert.Null(WebForwardedHeaders.CreateOptions(new WebHostConfig { TrustedProxies = new[] { "bad" } }));
        Assert.Null(WebForwardedHeaders.CreateOptions(null));
    }

    [Fact]
    public void Create_options_trusts_only_configured_proxies_and_one_hop()
    {
        var options = WebForwardedHeaders.CreateOptions(new WebHostConfig
        {
            TrustedProxies = new[] { "127.0.0.1" }
        });

        Assert.NotNull(options);
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options!.ForwardedHeaders);
        Assert.Equal(1, options.ForwardLimit);
        Assert.Contains(IPAddress.Loopback, options.KnownProxies);
        Assert.Empty(options.KnownNetworks);
    }
}
