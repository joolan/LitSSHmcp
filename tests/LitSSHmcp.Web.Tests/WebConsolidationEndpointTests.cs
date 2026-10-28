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

/// <summary>管理员 consolidation 审核 API：候选列表/批准/拒绝、事件列表/supersede、权限。</summary>
public class WebConsolidationEndpointTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"litssh-webcons-{Guid.NewGuid():N}.db");
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), $"litssh-webcons-log-{Guid.NewGuid():N}");
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private long _pendingId;
    private long _rejectId;
    private long _oldIncidentId;
    private long _newIncidentId;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new WebAccessLog(_logDir));
        builder.Services.AddSingleton<IWebMemoryStoreFactory>(new TestStoreFactory(_dbPath));
        builder.Services.AddSingleton<IConfigService>(new FakeWebConfigService());
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
        _app.MapWebConsolidationEndpoints();
        await _app.StartAsync();
        _client = _app.GetTestClient();

        var store = new AgentMemoryStore(_dbPath);
        await store.InitializeAsync();
        _pendingId = await store.AddConsolidationCandidateAsync("incident",
            JsonSerializer.Serialize(new
            {
                title = "磁盘满恢复",
                symptom = "df 100%",
                rootCause = "journald 膨胀",
                actions = "journalctl --vacuum-size=200M",
                outcome = "恢复",
                evidence = "log_query",
                status = "resolved"
            }), "ssh:web01", "raw:1", 0.8, "pending", "source=会话:s1;raw=1", "dedupe-pending")
            ?? throw new InvalidOperationException("seed candidate failed");
        _rejectId = await store.AddConsolidationCandidateAsync("incident",
            JsonSerializer.Serialize(new { title = "待拒绝事件", symptom = "test" }),
            "ssh:web02", "raw:2", 0.4, "pending", "source=会话:s2;raw=2", "dedupe-reject")
            ?? throw new InvalidOperationException("seed candidate failed");
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
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.SendAsync(Request(HttpMethod.Get, "/api/admin/consolidation/candidates", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.SendAsync(Request(HttpMethod.Get, "/api/admin/incidents", "user"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.SendAsync(Request(HttpMethod.Post,
                $"/api/admin/consolidation/candidates/{_pendingId}/approve", "user"))).StatusCode);
    }

    [Fact]
    public async Task Admin_can_list_and_approve_candidate()
    {
        var list = await _client.SendAsync(Request(HttpMethod.Get,
            "/api/admin/consolidation/candidates?status=pending", "admin"));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
            Assert.Equal(2, doc.RootElement.GetProperty("items").GetArrayLength());

        var approve = await _client.SendAsync(Request(HttpMethod.Post,
            $"/api/admin/consolidation/candidates/{_pendingId}/approve", "admin"));
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        using (var doc = JsonDocument.Parse(await approve.Content.ReadAsStringAsync()))
            Assert.True(doc.RootElement.GetProperty("incidentId").GetInt64() > 0);

        var incidents = await _client.SendAsync(Request(HttpMethod.Get, "/api/admin/incidents", "admin"));
        Assert.Equal(HttpStatusCode.OK, incidents.StatusCode);
        using (var doc = JsonDocument.Parse(await incidents.Content.ReadAsStringAsync()))
            Assert.Contains(doc.RootElement.GetProperty("items").EnumerateArray(),
                i => i.GetProperty("title").GetString() == "磁盘满恢复");

        var applied = await _client.SendAsync(Request(HttpMethod.Get,
            "/api/admin/consolidation/candidates?status=applied", "admin"));
        using (var doc = JsonDocument.Parse(await applied.Content.ReadAsStringAsync()))
            Assert.Equal(1, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Admin_can_list_consolidation_runs()
    {
        var store = new AgentMemoryStore(_dbPath, new MemoryAccessScope("9", IsAdmin: true));
        var runId = await store.StartConsolidationRunAsync();
        await store.FinishConsolidationRunAsync(runId, "completed", 7, 2, 1, "");

        var response = await _client.SendAsync(Request(HttpMethod.Get,
            "/api/admin/consolidation/runs?limit=5", "admin"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(7, item.GetProperty("rawCount").GetInt32());
        Assert.Equal(1, item.GetProperty("appliedCount").GetInt32());
    }

    [Fact]
    public async Task Admin_can_reject_candidate_with_reason()
    {
        var reject = await _client.SendAsync(Request(HttpMethod.Post,
            $"/api/admin/consolidation/candidates/{_rejectId}/reject", "admin",
            new { reason = "重复事件" }));

        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        var list = await _client.SendAsync(Request(HttpMethod.Get,
            "/api/admin/consolidation/candidates?status=rejected", "admin"));
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("重复事件", item.GetProperty("rejectReason").GetString());
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
