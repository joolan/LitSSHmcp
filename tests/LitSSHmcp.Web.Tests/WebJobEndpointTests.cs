using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LitSSHmcp.Core.Services.Web;
using LitSSHmcp.Web;
using LitSSHmcp.Web.Sessions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>Web 长任务 REST 端点的认证、归属和工具转发。</summary>
public sealed class WebJobEndpointTests : IClassFixture<WebJobEndpointFixture>
{
    private readonly WebJobEndpointFixture _fx;

    public WebJobEndpointTests(WebJobEndpointFixture fx) => _fx = fx;

    [Fact]
    public async Task Job_endpoints_require_login()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _fx.Client.GetAsync("/api/sessions/1/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _fx.Client.GetAsync("/api/sessions/1/jobs/job_1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _fx.Client.GetAsync("/api/sessions/1/jobs/job_1/result")).StatusCode);
    }

    [Fact]
    public async Task Owner_can_list_status_result_and_cancel_job()
    {
        var (token, csrf, sessionId) = await _fx.CreateSessionAsync();

        var list = await _fx.SendAsync(HttpMethod.Get,
            $"/api/sessions/{sessionId}/jobs?state=running&operation=topology_discover&limit=10", token, csrf);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal("host:job_list", await list.Content.ReadAsStringAsync());

        var status = await _fx.SendAsync(HttpMethod.Get,
            $"/api/sessions/{sessionId}/jobs/job_1", token, csrf);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("host:job_status", await status.Content.ReadAsStringAsync());

        var result = await _fx.SendAsync(HttpMethod.Get,
            $"/api/sessions/{sessionId}/jobs/job_1/result", token, csrf);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("host:job_result", await result.Content.ReadAsStringAsync());

        var cancel = await _fx.SendAsync(HttpMethod.Post,
            $"/api/sessions/{sessionId}/jobs/job_1/cancel", token, csrf);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal("host:job_cancel", await cancel.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_session_returns_404_for_job_endpoint()
    {
        var (token, csrf, _) = await _fx.CreateSessionAsync();
        var resp = await _fx.SendAsync(HttpMethod.Get, "/api/sessions/999999/jobs", token, csrf);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Foreign_user_gets_404_for_job_endpoint()
    {
        var (_, _, sessionId) = await _fx.CreateSessionAsync();
        var (otherToken, otherCsrf, _) = await _fx.CreateSessionAsync();

        var resp = await _fx.SendAsync(HttpMethod.Get,
            $"/api/sessions/{sessionId}/jobs", otherToken, otherCsrf);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}

public sealed class WebJobEndpointFixture : IDisposable
{
    public FakeRuntimeFactory RuntimeFactory { get; } = new();
    public WebApplicationFactory<Program> Factory { get; }
    public HttpClient Client { get; }

    public WebJobEndpointFixture()
    {
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWebRuntimeFactory>();
                services.AddSingleton<IWebRuntimeFactory>(RuntimeFactory);
            }));
        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
    }

    public async Task<(string Token, string Csrf, long SessionId)> CreateSessionAsync()
    {
        var username = "job" + Guid.NewGuid().ToString("N")[..12];
        await Factory.Services.GetRequiredService<WebAccountsStore>()
            .CreateAsync(username, "secret123", iterations: 1000);
        var login = await Client.PostAsJsonAsync("/api/login", new { username, password = "secret123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginDoc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = CookieFrom(login, "litssh_token")!;
        var csrf = loginDoc.RootElement.GetProperty("csrfToken").GetString()!;

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions")
        {
            Content = JsonContent.Create(new { title = "job endpoint" })
        };
        request.Headers.Add("Cookie", $"litssh_token={token}; litssh_csrf={csrf}");
        request.Headers.Add("X-CSRF-Token", csrf);
        var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (token, csrf, body.RootElement.GetProperty("id").GetInt64());
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, string csrf)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"litssh_token={token}; litssh_csrf={csrf}");
        if (method != HttpMethod.Get)
            request.Headers.Add("X-CSRF-Token", csrf);
        return await Client.SendAsync(request);
    }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
    }

    private static string? CookieFrom(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
            return null;
        foreach (var cookie in cookies)
        {
            if (cookie.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return cookie[(name.Length + 1)..].Split(';')[0];
        }
        return null;
    }
}
