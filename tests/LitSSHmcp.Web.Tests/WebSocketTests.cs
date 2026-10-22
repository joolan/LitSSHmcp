using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LitSSHmcp.Agent;
using LitSSHmcp.Core.Services.Web;
using LitSSHmcp.Web;
using LitSSHmcp.Web.Sessions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>
/// WebSocket 流式聊天集成测试（报告 D4/§3.2/§10.B.5）：
/// 用假运行时替换 <see cref="IWebRuntimeFactory"/>，验证认证/Origin/owner 隔离/帧协议/消息落库。
/// </summary>
public class WebSocketTests : IClassFixture<WsFixture>
{
    private readonly WsFixture _fx;

    public WebSocketTests(WsFixture fx) => _fx = fx;

    [Fact]
    public async Task Ws_connect_without_cookie_rejected()
    {
        var client = _fx.Factory.Server.CreateWebSocketClient();
        var uri = new UriBuilder(_fx.Factory.Server.BaseAddress) { Scheme = "ws", Path = "/ws/sessions/1" }.Uri;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(uri, CancellationToken.None));
        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ws_rejects_foreign_origin()
    {
        var (token, _) = await _fx.LoginAsync("ws-origin");
        var sessionId = await _fx.CreateSessionAsync(token, "会话");

        var client = _fx.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r =>
        {
            r.Headers["Cookie"] = $"litssh_token={token}";
            r.Headers["Origin"] = "http://evil.example";
        };
        var uri = WsUri(_fx, sessionId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(uri, CancellationToken.None));
        Assert.Contains("403", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ws_rejects_missing_origin()
    {
        var (token, _) = await _fx.LoginAsync("ws-no-origin");
        var sessionId = await _fx.CreateSessionAsync(token, "会话");

        var client = _fx.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers["Cookie"] = $"litssh_token={token}";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync(WsUri(_fx, sessionId), CancellationToken.None));
        Assert.Contains("403", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ws_connect_to_foreign_session_404()
    {
        var (aliceToken, _) = await _fx.LoginAsync("ws-alice");
        var sessionId = await _fx.CreateSessionAsync(aliceToken, "私有");
        var (bobToken, _) = await _fx.LoginAsync("ws-bob");

        var client = _fx.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r =>
        {
            r.Headers["Cookie"] = $"litssh_token={bobToken}";
            r.Headers["Origin"] = Origin(_fx);
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync(WsUri(_fx, sessionId), CancellationToken.None));
        Assert.Contains("404", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ws_streams_assistant_text_then_done_and_persists()
    {
        var (token, _) = await _fx.LoginAsync("ws-flow");
        var sessionId = await _fx.CreateSessionAsync(token, "流式");
        _fx.Fake.OnSend = (text, progress, _) =>
        {
            progress!.Report(new AgentEvent(AgentEventKind.ToolCall, ToolName: "bash", ArgumentsJson: "{}"));
            progress.Report(new AgentEvent(AgentEventKind.AssistantText, Text: "回:" + text));
            return Task.CompletedTask;
        };
        try
        {
            var socket = await ConnectAsync(token, sessionId);
            await SendFrameAsync(socket, new { type = "user", text = "你好" });

            var frames = new List<JsonElement>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var frame = await ReceiveFrameAsync(socket, cts.Token);
                frames.Add(frame);
                if (frame.GetProperty("type").GetString() == "done")
                    break;
            }

            var types = frames.Select(f => f.GetProperty("type").GetString()).ToArray();
            Assert.Equal(new[] { "tool", "assistant_text", "done" }, types);
            Assert.Equal("bash", frames[0].GetProperty("tool").GetString());
            Assert.Equal("回:你好", frames[1].GetProperty("text").GetString());
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

            // 消息已落库，REST 可读
            var messages = await GetMessagesAsync(token, sessionId);
            Assert.Equal(("user", "你好"), (messages[0].GetProperty("role").GetString(), messages[0].GetProperty("content").GetString()));
            Assert.Equal(("assistant", "回:你好"), (messages[1].GetProperty("role").GetString(), messages[1].GetProperty("content").GetString()));
        }
        finally
        {
            _fx.Fake.OnSend = null;
        }
    }

    [Fact]
    public async Task Ws_second_connection_while_busy_gets_busy_frame()
    {
        var (token, _) = await _fx.LoginAsync("ws-busy");
        var sessionId = await _fx.CreateSessionAsync(token, "忙");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fx.Fake.OnSend = async (_, progress, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            progress!.Report(new AgentEvent(AgentEventKind.AssistantText, Text: "完"));
        };
        try
        {
            // 第一条连接的轮次挂起中，第二条连接对同一会话发消息 → busy 帧
            var socket1 = await ConnectAsync(token, sessionId);
            await SendFrameAsync(socket1, new { type = "user", text = "慢" });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var socket2 = await ConnectAsync(token, sessionId);
            await SendFrameAsync(socket2, new { type = "user", text = "又" });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var busy = await ReceiveFrameAsync(socket2, cts.Token);
            Assert.Equal("busy", busy.GetProperty("type").GetString());

            release.TrySetResult();
            var frames = new List<JsonElement>();
            while (true)
            {
                var frame = await ReceiveFrameAsync(socket1, cts.Token);
                frames.Add(frame);
                if (frame.GetProperty("type").GetString() == "done")
                    break;
            }
            Assert.Contains(frames, f => f.GetProperty("type").GetString() == "assistant_text");
            await socket1.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            await socket2.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        finally
        {
            _fx.Fake.OnSend = null;
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Ws_stop_during_turn_is_processed_immediately()
    {
        var (token, _) = await _fx.LoginAsync("ws-stopturn");
        var sessionId = await _fx.CreateSessionAsync(token, "停止");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fx.Fake.OnSend = async (_, _, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        try
        {
            var socket = await ConnectAsync(token, sessionId);
            await SendFrameAsync(socket, new { type = "user", text = "长跑" });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // 回合进行中发 stop：接收循环必须未被阻塞，stop 立即生效 → done(stopped)
            await SendFrameAsync(socket, new { type = "stop" });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            JsonElement frame;
            do
            {
                frame = await ReceiveFrameAsync(socket, cts.Token);
            }
            while (frame.GetProperty("type").GetString() != "done");

            Assert.True(frame.GetProperty("stopped").GetBoolean());
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        finally
        {
            _fx.Fake.OnSend = null;
        }
    }

    [Fact]
    public async Task Ws_attachment_upload_then_send_reaches_runtime()
    {
        var (token, csrf) = await _fx.LoginAsync("ws-image");
        var sessionId = await _fx.CreateSessionAsync(token, "图片");

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(TestImages.Png());
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "a.png");
        var upload = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{sessionId}/attachments") { Content = form };
        upload.Headers.Add("Cookie", $"litssh_token={token}; litssh_csrf={csrf}");
        upload.Headers.Add("X-CSRF-Token", csrf);
        var uploadResp = await _fx.Client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);
        var body = JsonDocument.Parse(await uploadResp.Content.ReadAsStringAsync()).RootElement;
        var path = body.GetProperty("path").GetString();
        Assert.StartsWith("attachments/", path);
        Assert.Equal("image/png", body.GetProperty("contentType").GetString());

        var socket = await ConnectAsync(token, sessionId);
        await SendFrameAsync(socket, new
        {
            type = "user",
            text = "看图",
            attachments = new[] { new { path, contentType = "image/png" } }
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var frame = await ReceiveFrameAsync(socket, cts.Token);
            if (frame.GetProperty("type").GetString() == "done")
                break;
        }

        Assert.NotNull(_fx.Fake.LastContents);
        Assert.Equal(2, _fx.Fake.LastContents!.Count);       // 文本 + 1 张图片
        Assert.Single(_fx.Fake.LastContents!.OfType<Microsoft.Extensions.AI.DataContent>());
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }

    [Fact]
    public async Task Ws_unknown_frame_type_gets_error()
    {
        var (token, _) = await _fx.LoginAsync("ws-badframe");
        var sessionId = await _fx.CreateSessionAsync(token, "坏帧");

        var socket = await ConnectAsync(token, sessionId);
        await SendFrameAsync(socket, new { type = "hack" });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var frame = await ReceiveFrameAsync(socket, cts.Token);
        Assert.Equal("error", frame.GetProperty("type").GetString());
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }

    [Fact]
    public async Task Rest_session_endpoints_owner_isolated()
    {
        var (aliceToken, aliceCsrf) = await _fx.LoginAsync("rest-alice");
        var (bobToken, bobCsrf) = await _fx.LoginAsync("rest-bob");
        var sessionId = await _fx.CreateSessionAsync(aliceToken, "隔离");

        // bob 看不到 alice 的会话
        var bobList = await _fx.Client.SendAsync(Authed(HttpMethod.Get, "/api/sessions", bobToken, bobCsrf));
        var bobSessions = await bobList.Content.ReadFromJsonAsync<List<WebSessionInfo>>();
        Assert.DoesNotContain(bobSessions!, s => s.Id == sessionId);

        // bob 读消息/删除 → 404
        var read = await _fx.Client.SendAsync(Authed(HttpMethod.Get, $"/api/sessions/{sessionId}/messages", bobToken, bobCsrf));
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        var del = await _fx.Client.SendAsync(Authed(HttpMethod.Delete, $"/api/sessions/{sessionId}", bobToken, bobCsrf));
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);

        // alice 读消息/删除 → 正常
        var ok = await _fx.Client.SendAsync(Authed(HttpMethod.Get, $"/api/sessions/{sessionId}/messages", aliceToken, aliceCsrf));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var delOk = await _fx.Client.SendAsync(Authed(HttpMethod.Delete, $"/api/sessions/{sessionId}", aliceToken, aliceCsrf));
        Assert.Equal(HttpStatusCode.NoContent, delOk.StatusCode);
    }

    [Fact]
    public async Task Rest_create_requires_csrf()
    {
        var (token, _) = await _fx.LoginAsync("rest-csrf");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/sessions");
        req.Headers.Add("Cookie", $"litssh_token={token}");
        var resp = await _fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    private static Uri WsUri(WsFixture fx, long id)
        => new(fx.Factory.Server.BaseAddress!, $"ws://localhost/ws/sessions/{id}");

    private static string Origin(WsFixture fx)
        => fx.Factory.Server.BaseAddress!.GetLeftPart(UriPartial.Authority);

    private async Task<WebSocket> ConnectAsync(string token, long sessionId)
    {
        var client = _fx.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r =>
        {
            r.Headers["Cookie"] = $"litssh_token={token}";
            r.Headers["Origin"] = Origin(_fx);
        };
        return await client.ConnectAsync(WsUri(_fx, sessionId), CancellationToken.None);
    }

    private static async Task SendFrameAsync(WebSocket socket, object frame)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<JsonElement> ReceiveFrameAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }
        return JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray())).RootElement.Clone();
    }

    private async Task<JsonElement[]> GetMessagesAsync(string token, long sessionId)
    {
        var csrf = _fx.CsrfByToken[token];
        var resp = await _fx.Client.SendAsync(Authed(HttpMethod.Get, $"/api/sessions/{sessionId}/messages", token, csrf));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.EnumerateArray()
            .Select(e => e.Clone()).ToArray();
    }

    private static HttpRequestMessage Authed(HttpMethod method, string path, string token, string csrf)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add("Cookie", $"litssh_token={token}; litssh_csrf={csrf}");
        req.Headers.Add("X-CSRF-Token", csrf);
        return req;
    }
}

/// <summary>共享工厂：假运行时 + 独立 web 用户命名空间。</summary>
public sealed class WsFixture : IWebRuntimeFactory, IDisposable
{
    private readonly Dictionary<string, string> _loginCsrf = new();
    public FakeRuntimeFactory Fake { get; } = new();

    public WebApplicationFactory<Program> Factory { get; }
    public HttpClient Client { get; }

    public IReadOnlyDictionary<string, string> CsrfByToken => _loginCsrf;

    public WsFixture()
    {
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(services =>
            {
                services.RemoveAll<IWebRuntimeFactory>();
                services.AddSingleton<IWebRuntimeFactory>(this);
            }));
        // 关闭自动 Cookie 容器：测试手工携带 Cookie，避免多用户登录后容器污染请求头
        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
    }

    public Task<IWebRuntime> CreateAsync(WebRuntimeRequest request, CancellationToken ct)
        => Fake.CreateAsync(request, ct);

    public async Task<(string Token, string Csrf)> LoginAsync(string username)
    {
        await Factory.Services.GetRequiredService<WebAccountsStore>()
            .CreateAsync(username, "secret123", iterations: 1000);
        var resp = await Client.PostAsJsonAsync("/api/login", new { username, password = "secret123" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var csrf = body.RootElement.GetProperty("csrfToken").GetString()!;
        var token = CookieFrom(resp, "litssh_token")!;
        _loginCsrf[token] = csrf;
        return (token, csrf);
    }

    public async Task<long> CreateSessionAsync(string token, string title)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/sessions")
        {
            Content = JsonContent.Create(new { title })
        };
        req.Headers.Add("Cookie", $"litssh_token={token}; litssh_csrf={_loginCsrf[token]}");
        req.Headers.Add("X-CSRF-Token", _loginCsrf[token]);
        var resp = await Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetInt64();
    }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
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
