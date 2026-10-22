using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LitSSHmcp.Core.Services.Approval;
using LitSSHmcp.Web;
using LitSSHmcp.Web.Approvals;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>
/// 审批 REST/WS 端点集成测试（报告 §3.3.1/§10.B.1/B.5）：认证、CSRF、owner 隔离（404 不泄露存在性）、
/// 决策落盘、WS 快照。hub 换成临时目录实例（关闭后台轮询，行为确定）。
/// </summary>
public class WebApprovalEndpointTests : IClassFixture<ApprovalFixture>
{
    private readonly ApprovalFixture _fx;

    public WebApprovalEndpointTests(ApprovalFixture fx) => _fx = fx;

    [Fact]
    public async Task Get_list_requires_authentication()
    {
        var resp = await _fx.Client.GetAsync("/api/approvals");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Get_list_shows_only_own_pending()
    {
        var (aliceToken, aliceCsrf, aliceId) = await _fx.LoginAsync("appr-alice");
        var (_, _, bobId) = await _fx.LoginAsync("appr-bob");
        var ownId = _fx.SeedPending(aliceId);
        _fx.SeedPending(bobId);
        _fx.SeedPending("local");

        var resp = await _fx.Client.SendAsync(Authed(HttpMethod.Get, "/api/approvals", aliceToken, aliceCsrf));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var items = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("items");
        var ids = items.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToArray();
        Assert.Equal(new[] { ownId }, ids);
    }

    [Fact]
    public async Task Post_decide_requires_csrf()
    {
        var (token, _, userId) = await _fx.LoginAsync("appr-csrf");
        var id = _fx.SeedPending(userId);

        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/approvals/{id}")
        {
            Content = JsonContent.Create(new { approved = true })
        };
        req.Headers.Add("Cookie", $"litssh_token={token}");
        var resp = await _fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Null(ApprovalFileStore.TryReadDecision(_fx.ApprovalsDir, id));
    }

    [Fact]
    public async Task Post_decide_own_writes_web_decision()
    {
        var (token, csrf, userId) = await _fx.LoginAsync("appr-ok");
        var id = _fx.SeedPending(userId);

        var resp = await _fx.Client.SendAsync(
            Authed(HttpMethod.Post, $"/api/approvals/{id}", token, csrf, new { approved = true }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var decision = ApprovalFileStore.TryReadDecision(_fx.ApprovalsDir, id);
        Assert.NotNull(decision);
        Assert.True(decision!.Approved);
        Assert.Equal("web", decision.Channel);
    }

    [Fact]
    public async Task Post_decide_foreign_pending_404_and_no_decision()
    {
        var (bobToken, bobCsrf, _) = await _fx.LoginAsync("appr-foreign-bob");
        var (_, _, aliceId) = await _fx.LoginAsync("appr-foreign-alice");
        var alicePending = _fx.SeedPending(aliceId);

        var resp = await _fx.Client.SendAsync(
            Authed(HttpMethod.Post, $"/api/approvals/{alicePending}", bobToken, bobCsrf, new { approved = true }));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Null(ApprovalFileStore.TryReadDecision(_fx.ApprovalsDir, alicePending));
    }

    [Fact]
    public async Task Post_decide_unknown_id_404()
    {
        var (token, csrf, _) = await _fx.LoginAsync("appr-unknown");

        var resp = await _fx.Client.SendAsync(
            Authed(HttpMethod.Post, "/api/approvals/deadbeef", token, csrf, new { approved = true }));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Post_decide_missing_flag_400()
    {
        var (token, csrf, userId) = await _fx.LoginAsync("appr-badbody");
        var id = _fx.SeedPending(userId);

        var resp = await _fx.Client.SendAsync(
            Authed(HttpMethod.Post, $"/api/approvals/{id}", token, csrf, new { }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(ApprovalFileStore.TryReadDecision(_fx.ApprovalsDir, id));
    }

    [Fact]
    public async Task Ws_requires_authentication()
    {
        var client = _fx.Factory.Server.CreateWebSocketClient();
        var uri = new UriBuilder(_fx.Factory.Server.BaseAddress) { Scheme = "ws", Path = "/ws/approvals" }.Uri;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(uri, CancellationToken.None));
        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ws_snapshot_contains_only_own_pending()
    {
        var (token, _, userId) = await _fx.LoginAsync("appr-ws");
        var otherId = _fx.SeedPending("999");
        var ownId = _fx.SeedPending(userId);

        var client = _fx.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r =>
        {
            r.Headers["Cookie"] = $"litssh_token={token}";
            r.Headers["Origin"] = _fx.Factory.Server.BaseAddress!.GetLeftPart(UriPartial.Authority);
        };
        var uri = new UriBuilder(_fx.Factory.Server.BaseAddress) { Scheme = "ws", Path = "/ws/approvals" }.Uri;
        var socket = await client.ConnectAsync(uri, CancellationToken.None);

        var frame = await ReceiveFrameAsync(socket, CancellationToken.None);
        Assert.Equal("approval_snapshot", frame.GetProperty("type").GetString());
        var items = frame.GetProperty("items");
        var ids = items.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToArray();
        Assert.Equal(new[] { ownId }, ids);
        Assert.DoesNotContain(otherId, ids);

        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }

    private static HttpRequestMessage Authed(HttpMethod method, string path, string token, string csrf, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (body is not null)
            req.Content = JsonContent.Create(body);
        req.Headers.Add("Cookie", $"litssh_token={token}; litssh_csrf={csrf}");
        req.Headers.Add("X-CSRF-Token", csrf);
        return req;
    }

    private static async Task<JsonElement> ReceiveFrameAsync(System.Net.WebSockets.WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Text, result.MessageType);
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }
        return JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(ms.ToArray())).RootElement.Clone();
    }
}

/// <summary>共享工厂：审批 hub 换临时目录（关轮询），其余与生产一致。</summary>
public sealed class ApprovalFixture : IDisposable
{
    public string ApprovalsDir { get; } =
        Path.Combine(Path.GetTempPath(), "litssh-apprfx-" + Guid.NewGuid().ToString("N"));

    public WebApplicationFactory<Program> Factory { get; }
    public HttpClient Client { get; }

    public ApprovalFixture()
    {
        var dir = ApprovalsDir;
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(services =>
            {
                services.RemoveAll<WebApprovalHub>();
                services.AddSingleton(new WebApprovalHub(dir, new WebAccessLog(), TimeSpan.Zero));
            }));
        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
    }

    public async Task<(string Token, string Csrf, long UserId)> LoginAsync(string username)
    {
        await Factory.Services.GetRequiredService<LitSSHmcp.Core.Services.Web.WebAccountsStore>()
            .CreateAsync(username, "secret123", iterations: 1000);
        var resp = await Client.PostAsJsonAsync("/api/login", new { username, password = "secret123" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var csrf = body.RootElement.GetProperty("csrfToken").GetString()!;
        var userId = body.RootElement.GetProperty("user").GetProperty("id").GetInt64();
        var token = CookieFrom(resp, "litssh_token")!;
        return (token, csrf, userId);
    }

    /// <summary>种一条 pending（Owner=给定用户 Id 或 "local"），返回 id。</summary>
    public string SeedPending(long ownerId) => SeedPending(ownerId.ToString());

    public string SeedPending(string owner)
    {
        var id = Guid.NewGuid().ToString("N");
        ApprovalFileStore.WriteRequest(ApprovalsDir, new ApprovalRequestFile
        {
            Id = id,
            Server = "server-1",
            Operation = "exec",
            Command = "ls -la",
            CreatedAt = DateTimeOffset.Now,
            ExpiresAt = DateTimeOffset.Now.AddMinutes(1),
            TimeoutSeconds = 60,
            Owner = owner
        });
        return id;
    }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
        try { Directory.Delete(ApprovalsDir, recursive: true); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
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
