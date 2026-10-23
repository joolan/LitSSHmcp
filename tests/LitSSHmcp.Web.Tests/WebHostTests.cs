using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LitSSHmcp.Core.Services.Web;
using LitSSHmcp.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LitSSHmcp.Web.Tests;

internal static class WebTestEnv
{
    /// <summary>整个测试程序集共用一个临时数据目录（ConfigPaths 是进程级静态，须在任何访问前设置）。</summary>
    [ModuleInitializer]
    public static void Init()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-web-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("LITSSH_DATA_DIR", dir);
        DataDir = dir;
    }

    public static string DataDir { get; private set; } = string.Empty;
}

public class WebHostTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WebHostTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private WebAccountsStore Store => _factory.Services.GetRequiredService<WebAccountsStore>();

    [Fact]
    public async Task Health_returns_ok_without_auth()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("ok", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_success_sets_cookies_and_me_returns_user()
    {
        await Store.CreateAsync("alice", "secret123", iterations: 1000);
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/login", new { username = "alice", password = "secret123" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("alice", body.RootElement.GetProperty("user").GetProperty("username").GetString());
        Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("csrfToken").GetString()));
        var csrf = body.RootElement.GetProperty("csrfToken").GetString()!;

        var tokenCookie = CookieFrom(resp, "litssh_token");
        Assert.False(string.IsNullOrEmpty(tokenCookie));
        Assert.Contains("httponly", resp.Headers.TryGetValues("Set-Cookie", out var setCookies)
            ? string.Join(";", setCookies).ToLowerInvariant() : string.Empty, StringComparison.Ordinal);

        var me = await client.SendAsync(Authed(new HttpRequestMessage(HttpMethod.Get, "/api/me"), tokenCookie!, csrf));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var meBody = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.Equal("alice", meBody.RootElement.GetProperty("username").GetString());
        Assert.Equal("user", meBody.RootElement.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Login_wrong_password_401_and_logged()
    {
        await Store.CreateAsync("bob", "secret123", iterations: 1000);
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/login", new { username = "bob", password = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

        var logFile = Directory.GetFiles(Path.Combine(WebTestEnv.DataDir, "logs"), "web-access-*.log").FirstOrDefault();
        Assert.NotNull(logFile);
        Assert.Contains("login_failure", await File.ReadAllTextAsync(logFile!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_locked_after_five_failures_423()
    {
        await Store.CreateAsync("carol", "secret123", iterations: 1000);
        var client = _factory.CreateClient();

        for (var i = 0; i < WebAuthPolicy.MaxFailedAttempts - 1; i++)
        {
            var fail = await client.PostAsJsonAsync("/api/login", new { username = "carol", password = "nope" });
            Assert.Equal(HttpStatusCode.Unauthorized, fail.StatusCode);
        }
        var fifth = await client.PostAsJsonAsync("/api/login", new { username = "carol", password = "nope" });
        Assert.Equal((HttpStatusCode)423, fifth.StatusCode);

        var lockedBody = JsonDocument.Parse(await fifth.Content.ReadAsStringAsync());
        Assert.True(lockedBody.RootElement.GetProperty("retryAfterSeconds").GetInt32() > 0);

        // 锁定期内正确密码同样 423
        var correct = await client.PostAsJsonAsync("/api/login", new { username = "carol", password = "secret123" });
        Assert.Equal((HttpStatusCode)423, correct.StatusCode);
    }

    [Fact]
    public async Task Me_without_cookie_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_token_then_me_401()
    {
        await Store.CreateAsync("dave", "secret123", iterations: 1000);
        var client = _factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/login", new { username = "dave", password = "secret123" });
        var token = CookieFrom(login, "litssh_token")!;
        var csrf = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("csrfToken").GetString()!;

        var logout = await client.SendAsync(Authed(new HttpRequestMessage(HttpMethod.Post, "/api/logout"), token, csrf));
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        var me = await client.SendAsync(Authed(new HttpRequestMessage(HttpMethod.Get, "/api/me"), token, csrf));
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task Csrf_missing_header_on_logout_403()
    {
        await Store.CreateAsync("erin", "secret123", iterations: 1000);
        var client = _factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/login", new { username = "erin", password = "secret123" });
        var token = CookieFrom(login, "litssh_token")!;

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/logout");
        req.Headers.Add("Cookie", $"litssh_token={token}");
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_first_user_becomes_admin()
    {
        var store = new WebAccountsStore(Path.Combine(WebTestEnv.DataDir, "bootstrap.db"));
        await store.InitializeAsync();

        Assert.Null(await WebBootstrap.CreateUserAsync(store, "rootadmin", role: null, password: "secret123"));
        Assert.Equal("admin", (await store.FindByUsernameAsync("rootadmin"))!.Role);

        Assert.Null(await WebBootstrap.CreateUserAsync(store, "second", role: null, password: "secret123"));
        Assert.Equal("user", (await store.FindByUsernameAsync("second"))!.Role);

        Assert.NotNull(await WebBootstrap.CreateUserAsync(store, "x", role: null, password: null));
    }

    private static HttpRequestMessage Authed(HttpRequestMessage req, string tokenCookie, string csrf)
    {
        req.Headers.Add("Cookie", $"litssh_token={tokenCookie}");
        req.Headers.Add("X-CSRF-Token", csrf);
        return req;
    }

    private static string? CookieFrom(HttpResponseMessage resp, string name)
    {
        if (!resp.Headers.TryGetValues("Set-Cookie", out var cookies)) return null;
        foreach (var c in cookies)
        {
            if (c.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return c[(name.Length + 1)..].Split(';')[0];
        }
        return null;
    }
}
