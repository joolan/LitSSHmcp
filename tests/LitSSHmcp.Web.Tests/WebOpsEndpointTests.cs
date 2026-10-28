using System.Net;
using System.Text.Json;
using LitSSHmcp.Agent;
using LitSSHmcp.Agent.Sessions;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Web;
using LitSSHmcp.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>运维视图 API：事件浏览/时间线/资产画像（登录用户；按团队/私有作用域）。</summary>
public class WebOpsEndpointTests : IAsyncLifetime
{
    private readonly string _memoryPath = Path.Combine(Path.GetTempPath(), $"litssh-webops-{Guid.NewGuid():N}.db");
    private readonly string _contextPath = Path.Combine(Path.GetTempPath(), $"litssh-webops-ctx-{Guid.NewGuid():N}.db");
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), $"litssh-webops-log-{Guid.NewGuid():N}");
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private long _incidentId;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new WebAccessLog(_logDir));
        builder.Services.AddSingleton<IWebMemoryStoreFactory>(new TestStoreFactory(_memoryPath));
        builder.Services.AddSingleton<IConfigService>(new FakeWebConfigService());
        var context = new ContextStore(_contextPath);
        await context.InitializeAsync();
        builder.Services.AddSingleton<IContextStore>(context);
        _app = builder.Build();
        _app.Use(async (ctx, next) =>
        {
            var role = ctx.Request.Headers["X-Test-Role"].ToString();
            if (!string.IsNullOrEmpty(role))
                ctx.Items[WebAuthItems.Principal] = new WebPrincipal(role == "admin" ? 9 : 1,
                    role == "admin" ? "admin" : "alice", role);
            await next();
        });
        _app.MapWebOpsEndpoints();
        await _app.StartAsync();
        _client = _app.GetTestClient();

        var sessionId = await context.CreateSessionAsync("测试会话");
        await context.AppendMessageAsync(sessionId, "user", "磁盘满怎么办");
        await context.AppendMessageAsync(sessionId, "tool",
            """{"Tool":"ssh_snapshot_get","Args":"{}","Result":"df 100%","Ok":true,"Ms":12,"IsInfo":false}""");
        await context.AppendMessageAsync(sessionId, "assistant", "已恢复");

        var store = new AgentMemoryStore(_memoryPath);
        await store.InitializeAsync();
        _incidentId = await store.AddIncidentAsync("磁盘满恢复", "df 100%", "ssh:web01", "journald",
            "清理日志", "恢复", "raw:1", 0.8, "resolved", $"agent-{sessionId}");
        await store.AddStructuredAsync("asset", "资产事实:ssh:web01:日志路径", "会话",
            "ssh:web01 的日志路径是 /var/log/app.log", new[] { 1f, 0f }, "ssh:web01", "", 1);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        foreach (var path in new[] { _memoryPath, _contextPath })
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(path + suffix); } catch { /* ignore */ }
        try { Directory.Delete(_logDir, recursive: true); } catch { /* ignore */ }
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? role)
    {
        var req = new HttpRequestMessage(method, path);
        if (role is not null) req.Headers.Add("X-Test-Role", role);
        return req;
    }

    [Fact]
    public async Task Endpoints_require_login()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.SendAsync(Request(HttpMethod.Get, "/api/ops/incidents", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.SendAsync(Request(HttpMethod.Get, "/api/ops/timeline?incidentId=1", null))).StatusCode);
    }

    [Fact]
    public async Task User_can_list_incident_and_load_timeline()
    {
        var list = await _client.SendAsync(Request(HttpMethod.Get, "/api/ops/incidents", "user"));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
            Assert.Contains(doc.RootElement.GetProperty("items").EnumerateArray(),
                i => i.GetProperty("title").GetString() == "磁盘满恢复");

        var timeline = await _client.SendAsync(Request(HttpMethod.Get,
            $"/api/ops/timeline?incidentId={_incidentId}", "user"));
        Assert.Equal(HttpStatusCode.OK, timeline.StatusCode);
        using var timelineDoc = JsonDocument.Parse(await timeline.Content.ReadAsStringAsync());
        var text = timelineDoc.RootElement.GetProperty("text").GetString() ?? string.Empty;
        Assert.Contains("## 事件", text);
        Assert.Contains("工具 ssh_snapshot_get 成功", text);
        Assert.Contains("助手: 已恢复", text);
    }

    [Fact]
    public async Task User_can_load_asset_profile()
    {
        var resp = await _client.SendAsync(Request(HttpMethod.Get,
            "/api/ops/asset-profile?assetId=ssh:web01", "user"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var text = doc.RootElement.GetProperty("text").GetString() ?? string.Empty;
        Assert.Contains("## 资产画像 ssh:web01", text);
        Assert.Contains("日志路径", text);
        Assert.Contains("/var/log/app.log", text);
        Assert.Contains("磁盘满恢复", text);
    }

    private sealed class TestStoreFactory(string dbPath) : IWebMemoryStoreFactory
    {
        public IAgentMemoryStore Create(string ownerUserId, bool isAdmin) =>
            new AgentMemoryStore(dbPath, new MemoryAccessScope(ownerUserId, isAdmin));
    }
}
