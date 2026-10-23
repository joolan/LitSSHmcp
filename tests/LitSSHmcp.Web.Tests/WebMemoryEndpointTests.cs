using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LitSSHmcp.Agent;
using LitSSHmcp.Core.Services.Web;
using LitSSHmcp.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>管理员记忆库 API：权限（admin only）、历史版本、回滚、归档。</summary>
public class WebMemoryEndpointTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"litssh-webmem-{Guid.NewGuid():N}.db");
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), $"litssh-webmem-log-{Guid.NewGuid():N}");
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private long _memoryId;

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
        _app.MapWebMemoryEndpoints();
        await _app.StartAsync();
        _client = _app.GetTestClient();

        var store = new AgentMemoryStore(_dbPath);
        await store.InitializeAsync();
        _memoryId = await store.AddStructuredAsync("task", "disk", "会话", "v1 磁盘经验",
            new[] { 1f, 0f }, "ssh:s1", "nginx", 1);
        await store.UpdateStructuredAsync(_memoryId, "v2 磁盘经验", new[] { 0f, 1f }, "ssh:s2", "nginx",
            "active", 0);
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
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(Request(HttpMethod.Get, "/api/admin/memory", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(Request(HttpMethod.Get, "/api/admin/memory", "user"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.SendAsync(Request(HttpMethod.Post, $"/api/admin/memory/{_memoryId}/archive", "user"))).StatusCode);
    }

    [Fact]
    public async Task Admin_can_list_versions_and_rollback()
    {
        var list = await _client.SendAsync(Request(HttpMethod.Get, "/api/admin/memory", "admin"));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            var items = doc.RootElement.GetProperty("items");
            Assert.Equal(1, items.GetArrayLength());
            Assert.Equal("task", items[0].GetProperty("kind").GetString());
            Assert.Equal("disk", items[0].GetProperty("key").GetString());
        }

        var versions = await _client.SendAsync(Request(HttpMethod.Get, $"/api/admin/memory/{_memoryId}/versions", "admin"));
        Assert.Equal(HttpStatusCode.OK, versions.StatusCode);
        using (var doc = JsonDocument.Parse(await versions.Content.ReadAsStringAsync()))
        {
            var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(1, item.GetProperty("version").GetInt32());
            Assert.Equal("v1 磁盘经验", item.GetProperty("content").GetString());
        }

        var rollback = await _client.SendAsync(Request(HttpMethod.Post, $"/api/admin/memory/{_memoryId}/rollback",
            "admin", new { version = 1 }));
        Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);
        var store = new AgentMemoryStore(_dbPath);
        var restored = await store.FindByKeyAsync("task", "disk");
        Assert.Equal("v1 磁盘经验", restored!.Content);
    }

    [Fact]
    public async Task Admin_can_prune_history_to_keep_recent_versions()
    {
        var store = new AgentMemoryStore(_dbPath);
        for (var i = 0; i < 5; i++)
            await store.UpdateStructuredAsync(_memoryId, $"v{i + 3} 磁盘经验", new[] { 1f, 0f },
                "ssh:s1", "nginx", "active", 0);

        var resp = await _client.SendAsync(Request(HttpMethod.Post, "/api/admin/memory/history/prune",
            "admin", new { keep = 2 }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using (var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()))
            Assert.True(doc.RootElement.GetProperty("removed").GetInt32() > 0);

        var versions = await _client.SendAsync(Request(HttpMethod.Get,
            $"/api/admin/memory/{_memoryId}/versions", "admin"));
        using var versionsDoc = JsonDocument.Parse(await versions.Content.ReadAsStringAsync());
        Assert.Equal(2, versionsDoc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Archive_marks_superseded_and_unknown_version_returns_404()
    {
        var missing = await _client.SendAsync(Request(HttpMethod.Post, $"/api/admin/memory/{_memoryId}/rollback",
            "admin", new { version = 99 }));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var archive = await _client.SendAsync(Request(HttpMethod.Post, $"/api/admin/memory/{_memoryId}/archive", "admin"));
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        var store = new AgentMemoryStore(_dbPath);
        Assert.Equal("superseded", (await store.FindByKeyAsync("task", "disk"))!.Status);

        var again = await _client.SendAsync(Request(HttpMethod.Post, $"/api/admin/memory/999999/archive", "admin"));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    private sealed class TestStoreFactory(string dbPath) : IWebMemoryStoreFactory
    {
        public IAgentMemoryStore Create(string ownerUserId, bool isAdmin) =>
            new AgentMemoryStore(dbPath, new MemoryAccessScope(ownerUserId, isAdmin));
    }
}
