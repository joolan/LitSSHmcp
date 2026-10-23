using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>
/// 安全响应头集成测试（评估报告 §10.B 攻击面收敛）：
/// 全站 nosniff/DENY/no-referrer/Permissions-Policy；HTML 响应附与 index.html meta 同源的 CSP。
/// </summary>
public class SecurityHeadersTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SecurityHeadersTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Html_shell_has_security_headers_and_csp()
    {
        var resp = await _factory.CreateClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("nosniff", Header(resp, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(resp, "X-Frame-Options"));
        Assert.Equal("no-referrer", Header(resp, "Referrer-Policy"));
        Assert.Contains("camera=()", Header(resp, "Permissions-Policy"), StringComparison.Ordinal);
        var csp = Header(resp, "Content-Security-Policy");
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_responses_carry_security_headers_too()
    {
        var resp = await _factory.CreateClient().GetAsync("/api/sessions");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("nosniff", Header(resp, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(resp, "X-Frame-Options"));
    }

    [Fact]
    public async Task Auth_responses_are_not_cacheable()
    {
        var resp = await _factory.CreateClient().PostAsJsonAsync("/api/login",
            new { username = "nobody", password = "bad" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("no-store", Header(resp, "Cache-Control"));
    }

    [Fact]
    public async Task Non_html_responses_have_no_csp_header()
    {
        var resp = await _factory.CreateClient().GetAsync("/js/sanitize.js");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("nosniff", Header(resp, "X-Content-Type-Options"));
        Assert.False(resp.Headers.Contains("Content-Security-Policy"));
    }

    private static string Header(HttpResponseMessage resp, string name)
        => resp.Headers.TryGetValues(name, out var values)
            ? string.Join(", ", values)
            : string.Empty;
}
