using Microsoft.AspNetCore.Http;
using Xunit;

namespace LitSSHmcp.Web.Tests;

public class WebSocketOriginPolicyTests
{
    private static HttpContext Context(string scheme, string host)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = scheme;
        ctx.Request.Host = new HostString(host);
        return ctx;
    }

    [Fact]
    public void Allows_same_origin_with_default_https_port()
    {
        Assert.True(WebSocketOriginPolicy.IsAllowed(Context("https", "app.example"), "https://app.example"));
    }

    [Fact]
    public void Allows_same_origin_with_explicit_port()
    {
        Assert.True(WebSocketOriginPolicy.IsAllowed(Context("http", "localhost:8080"), "http://localhost:8080"));
    }

    [Fact]
    public void Rejects_foreign_host()
    {
        Assert.False(WebSocketOriginPolicy.IsAllowed(Context("https", "app.example"), "https://evil.example"));
    }

    [Fact]
    public void Rejects_scheme_downgrade()
    {
        Assert.False(WebSocketOriginPolicy.IsAllowed(Context("https", "app.example"), "http://app.example"));
    }

    [Fact]
    public void Rejects_port_mismatch()
    {
        Assert.False(WebSocketOriginPolicy.IsAllowed(Context("https", "app.example"), "https://app.example:8443"));
    }

    [Fact]
    public void Rejects_missing_origin()
    {
        Assert.False(WebSocketOriginPolicy.IsAllowed(Context("https", "app.example"), null));
        Assert.False(WebSocketOriginPolicy.IsAllowed(Context("https", "app.example"), ""));
        Assert.False(WebSocketOriginPolicy.IsAllowed(Context("https", "app.example"), "null"));
    }

    [Fact]
    public void Rejects_origin_with_path_query_or_userinfo()
    {
        var ctx = Context("https", "app.example");
        Assert.False(WebSocketOriginPolicy.IsAllowed(ctx, "https://app.example/ws"));
        Assert.False(WebSocketOriginPolicy.IsAllowed(ctx, "https://app.example?x=1"));
        Assert.False(WebSocketOriginPolicy.IsAllowed(ctx, "https://user@app.example"));
    }
}

public class WebSocketConnectionLimiterTests
{
    [Fact]
    public void Enforces_limit_per_key_and_releases_on_dispose()
    {
        var limiter = new WebSocketConnectionLimiter();

        var first = limiter.TryAcquire("user:1", 1);
        Assert.NotNull(first);
        Assert.Null(limiter.TryAcquire("user:1", 1));
        Assert.NotNull(limiter.TryAcquire("user:2", 1));

        first!.Dispose();
        first.Dispose();

        using var again = limiter.TryAcquire("user:1", 1);
        Assert.NotNull(again);
    }

    [Fact]
    public void Rejects_empty_key_or_non_positive_limit()
    {
        var limiter = new WebSocketConnectionLimiter();

        Assert.Null(limiter.TryAcquire(null, 1));
        Assert.Null(limiter.TryAcquire("", 1));
        Assert.Null(limiter.TryAcquire("user:1", 0));
    }
}
