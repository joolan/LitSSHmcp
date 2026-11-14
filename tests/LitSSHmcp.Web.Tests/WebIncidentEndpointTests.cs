using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LitSSHmcp.Agent;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Web;
using LitSSHmcp.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>管理员事件 API：事件列表/supersede、权限。</summary>
public class WebIncidentEndpointTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"litssh-webinc-{Guid.NewGuid():N}.db");
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), $"litssh-webinc-log-{Guid.NewGuid():N}");
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private long _oldIncidentId;
    private long _newIncidentId;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new WebAccessLog(_logDir));
        builder.Services.AddSingleton<IWebMemoryStoreFactory>(new TestStoreFactory(_dbPath));
        _app = builder.Build();
        _app.Use(async (ctx, next) =>
        {
            var role = ctx.Request.Headers["X-Test-Role"].ToString();
            if (!string.IsNullOrEmpty(role))
            {
                ctx.Items[WebAuthItems.Principal] = new WebPrincipal(
                    role == "admin" ? 9 : 1,
                    role == "admin" ? "admin" : "alice",
                    role);
            }
            await next();
        });
        _app.MapWebIncidentEndpoints();
        await _app.StartAsync();
        _client = _app.GetTestClient();

        var store = new AgentMemoryStore(_dbPath);
        await store.InitializeAsync();
        _oldIncidentId = await store.AddIncidentAsync("旧事件", "旧现象", "ssh:s1", "", "", "", "", 0.5,
            "resolved", "");
        _newIncidentId = await store.AddIncidentAsync("新事件", "新现象", "ssh:s1", "", "", "", "", 0.8,
            "resolved", "");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
        try { Directory.Delete(_logDir, recursive: true); } catch { /* ignore */ }
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? role, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (role is not null)
            req.Headers.Add("X-Test-Role", role);
        if (body is not null)
            req.Content = JsonContent.Create(body);
        return req;
    }

    [Fact]
    public async Task Endpoints_require_admin()
    {
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.SendAsync(Request(HttpMethod.Get, "/api/admin/incidents", "user"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.SendAsync(Request(HttpMethod.Post,
                $"/api/admin/incidents/{_oldIncidentId}/supersede", "user",
                new { newId = _newIncidentId }))).StatusCode);
    }

    [Fact]
    public async Task Admin_can_list_incidents()
    {
        var incidents = await _client.SendAsync(Request(HttpMethod.Get, "/api/admin/incidents", "admin"));
        Assert.Equal(HttpStatusCode.OK, incidents.StatusCode);
        using var doc = JsonDocument.Parse(await incidents.Content.ReadAsStringAsync());
        Assert.Contains(doc.RootElement.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("title").GetString() == "新事件");
    }

    [Fact]
    public async Task Admin_can_supersede_incident()
    {
        var resp = await _client.SendAsync(Request(HttpMethod.Post,
            $"/api/admin/incidents/{_oldIncidentId}/supersede", "admin",
            new { newId = _newIncidentId }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var store = new AgentMemoryStore(_dbPath);
        var old = await store.GetIncidentAsync(_oldIncidentId);
        Assert.Equal("superseded", old!.Status);
        Assert.Equal(_newIncidentId, old.SupersededBy);
    }

    private sealed class TestStoreFactory(string dbPath) : IWebMemoryStoreFactory
    {
        public IAgentMemoryStore Create(string ownerUserId, bool isAdmin) =>
            new AgentMemoryStore(dbPath, new MemoryAccessScope(ownerUserId, isAdmin));
    }
}
