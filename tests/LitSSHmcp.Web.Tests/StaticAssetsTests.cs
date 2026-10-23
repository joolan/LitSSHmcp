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
        // frame-ancestors 在 <meta> 中无效（浏览器会告警）；由响应头强制
        var head = html.Split("</head>", StringSplitOptions.None)[0];
        Assert.DoesNotContain("frame-ancestors", head, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Static_assets_are_revalidated_to_avoid_stale_js()
    {
        var resp = await _fx.Client.GetAsync("/js/chat.js");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("no-cache", resp.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Frontend_modules_are_served()
    {
        foreach (var path in new[] { "/js/app.js", "/js/sanitize.js", "/js/chat.js", "/js/approvals.js", "/js/files.js", "/js/api.js", "/js/memory.js", "/css/app.css" })
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
    public async Task Approval_panel_is_fixed_overlay_with_badge()
    {
        // 审批操作区固定悬浮（不随聊天滚动）+ 顶栏待审批徽标：长会话历史下也一定可见
        var html = await _fx.Client.GetStringAsync("/");
        Assert.Contains("id=\"approval-overlay\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"approval-badge\"", html, StringComparison.Ordinal);

        var css = await _fx.Client.GetStringAsync("/css/app.css");
        var normalized = System.Text.RegularExpressions.Regex.Replace(css, @"\s+", " ");
        Assert.Contains(".appr-overlay { position: fixed;", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mobile_layout_and_composer_attachments_present()
    {
        var html = await _fx.Client.GetStringAsync("/");
        foreach (var id in new[] { "menu-btn", "file-input", "attachment-chips", "model-sheet", "to-bottom", "load-older" })
            Assert.Contains($"id=\"{id}\"", html, StringComparison.Ordinal);
        // 桌面风格输入区：发送/停止合并按钮 + 模型/图片工具行（在输入框下方）
        Assert.Contains("class=\"primary send-stop\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"composer-toolbar\"", html, StringComparison.Ordinal);

        var css = await _fx.Client.GetStringAsync("/css/app.css");
        var normalized = System.Text.RegularExpressions.Regex.Replace(css, @"\s+", " ");
        Assert.Contains("@media (max-width: 820px)", normalized, StringComparison.Ordinal);
        Assert.Contains("body.drawer-open #session-pane", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Image_viewer_overlay_present()
    {
        var html = await _fx.Client.GetStringAsync("/");
        Assert.Contains("id=\"image-viewer\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"image-viewer-img\"", html, StringComparison.Ordinal);

        var css = await _fx.Client.GetStringAsync("/css/app.css");
        var normalized = System.Text.RegularExpressions.Regex.Replace(css, @"\s+", " ");
        Assert.Contains(".image-viewer { position: fixed;", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_memory_panel_entry_is_present_for_admin_only()
    {
        var html = await _fx.Client.GetStringAsync("/");
        Assert.Contains("id=\"memory-admin-btn\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"memory-admin\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"memory-list\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"memory-versions\"", html, StringComparison.Ordinal);

        var js = await _fx.Client.GetStringAsync("/js/memory.js");
        Assert.Contains("MemoryAdminPanel", js, StringComparison.Ordinal);
        Assert.Contains("/api/admin/memory", js, StringComparison.Ordinal);

        var css = await _fx.Client.GetStringAsync("/css/app.css");
        var normalized = System.Text.RegularExpressions.Regex.Replace(css, @"\s+", " ");
        Assert.Contains(".memory-sheet {", normalized, StringComparison.Ordinal);
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
