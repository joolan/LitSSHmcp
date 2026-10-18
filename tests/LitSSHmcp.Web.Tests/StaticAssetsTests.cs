using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>
/// SPA 静态资源集成测试（评估报告 §3.1 Web UI / §10.B.3）：
/// 首页与 JS/CSS 可匿名访问（登录页本就需要）、CSP meta 就位、API 仍强制认证。
/// </summary>
public class StaticAssetsTests : IClassFixture<StaticAssetsFixture>
{
    private readonly StaticAssetsFixture _fx;

    public StaticAssetsTests(StaticAssetsFixture fx) => _fx = fx;

    [Fact]
    public async Task Root_serves_index_html_with_csp()
    {
        var resp = await _fx.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Content-Security-Policy", html, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", html, StringComparison.Ordinal);
        Assert.Contains("/js/app.js", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Frontend_modules_are_served()
    {
        foreach (var path in new[] { "/js/app.js", "/js/sanitize.js", "/js/chat.js", "/js/approvals.js", "/js/files.js", "/js/api.js", "/css/app.css" })
        {
            var resp = await _fx.Client.GetAsync(path);
            Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{path} → {resp.StatusCode}");
        }
    }

    [Fact]
    public async Task Sanitize_js_is_the_escape_first_renderer()
    {
        var js = await _fx.Client.GetStringAsync("/js/sanitize.js");
        Assert.Contains("renderMarkdown", js, StringComparison.Ordinal);
        Assert.Contains("sanitizeUrl", js, StringComparison.Ordinal);
        Assert.Contains("mailto:", js, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Css_hidden_guard_overrides_author_display_rules()
    {
        // 登录窗/审批条带 display:flex 的作者样式会压过 UA 对 [hidden] 的 display:none
        // （曾导致 showApp() 后登录窗藏不住）——守卫规则须恒在。
        var css = await _fx.Client.GetStringAsync("/css/app.css");
        var normalized = System.Text.RegularExpressions.Regex.Replace(css, @"\s+", " ");
        Assert.Contains("[hidden] { display: none !important; }", normalized, StringComparison.Ordinal);
        Assert.Contains("#login-view { display: flex", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_still_requires_authenticaton()
    {
        var resp = await _fx.Client.GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

        var login = await _fx.Client.PostAsJsonAsync("/api/login", new { username = "nobody", password = "bad" });
        Assert.NotEqual(HttpStatusCode.OK, login.StatusCode);   // 匿名与错口令均不可用
    }
}

public sealed class StaticAssetsFixture : IDisposable
{
    public WebApplicationFactory<Program> Factory { get; }
    public HttpClient Client { get; }

    public StaticAssetsFixture()
    {
        Factory = new WebApplicationFactory<Program>();
        Client = Factory.CreateClient();
    }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
        GC.SuppressFinalize(this);
    }
}
