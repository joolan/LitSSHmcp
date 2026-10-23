using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using LitSSHmcp.App.Services.RemoteDesktop;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class VncWebHostTests
{
    [Fact]
    public async Task Serves_static_assets_with_js_mime_and_reports_404()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-vnc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "core"));
        await File.WriteAllTextAsync(Path.Combine(dir, "vnc_lite.html"), "<html>lite</html>");
        await File.WriteAllTextAsync(Path.Combine(dir, "core", "rfb.js"), "export default 1;");

        try
        {
            using var host = new VncWebHost(dir, "127.0.0.1", 5900);
            Assert.True(host.Start());

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };

            // 根路径回落到 vnc_lite.html
            Assert.Contains("lite", await http.GetStringAsync("/"));

            // 静态 JS：内容正确且 MIME 为 ES 模块可接受的 text/javascript
            var jsResp = await http.GetAsync("/core/rfb.js");
            Assert.Equal(HttpStatusCode.OK, jsResp.StatusCode);
            Assert.Equal("text/javascript", jsResp.Content.Headers.ContentType?.MediaType);
            Assert.Contains("export", await jsResp.Content.ReadAsStringAsync());
            Assert.Equal("nosniff", jsResp.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("no-referrer", jsResp.Headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("DENY", jsResp.Headers.GetValues("X-Frame-Options").Single());

            // 不存在的文件 → 404
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/missing.txt")).StatusCode);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Page_url_points_at_websockify_and_keeps_secrets_in_fragment()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-vnc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var host = new VncWebHost(dir, "127.0.0.1", 5900);
            Assert.True(host.Start());

            var url = host.PageUrl("p@ss w/rd");
            Assert.Contains("vnc_lite.html", url);
            Assert.Contains("path=websockify", url);
            Assert.Contains("port=" + host.Port, url);

            var beforeFragment = url.Split('#')[0];
            Assert.DoesNotContain("password=", beforeFragment);
            Assert.DoesNotContain("token=", beforeFragment);
            Assert.Contains("password=p%40ss%20w%2Frd", url);   // 仅在 fragment 中且已 URL 转义
            Assert.False(string.IsNullOrEmpty(FragmentParameter(url, "token")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public Task Bridge_rejects_missing_token()
        => AssertBridgeRejectedAsync(includeToken: false, includeOrigin: true, foreignOrigin: false);

    [Fact]
    public Task Bridge_rejects_missing_origin()
        => AssertBridgeRejectedAsync(includeToken: true, includeOrigin: false, foreignOrigin: false);

    [Fact]
    public Task Bridge_rejects_foreign_origin()
        => AssertBridgeRejectedAsync(includeToken: true, includeOrigin: true, foreignOrigin: true);

    private static async Task AssertBridgeRejectedAsync(bool includeToken, bool includeOrigin, bool foreignOrigin)
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-vnc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var host = new VncWebHost(dir, "127.0.0.1", 5900);
            Assert.True(host.Start());

            using var ws = new ClientWebSocket();
            if (includeOrigin)
            {
                var hostName = foreignOrigin ? "evil.example" : "127.0.0.1";
                ws.Options.SetRequestHeader("Origin", $"http://{hostName}:{host.Port}");
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var token = includeToken ? BridgeToken(host) : null;
            await Assert.ThrowsAnyAsync<Exception>(() => ws.ConnectAsync(BridgeUri(host, token), cts.Token));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Bridges_websocket_binary_frames_to_target_tcp()
    {
        // 伪装 VNC 目标：回环 TCP echo 服务
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var targetPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        var echo = Task.Run(async () =>
        {
            try
            {
                using var client = await tcp.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buf = new byte[1024];
                int n;
                while ((n = await stream.ReadAsync(buf)) > 0)
                    await stream.WriteAsync(buf.AsMemory(0, n));
            }
            catch
            {
                // 断开等异常忽略
            }
        });

        var dir = Path.Combine(Path.GetTempPath(), "litssh-vnc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var host = new VncWebHost(dir, "127.0.0.1", targetPort);
            Assert.True(host.Start());

            using var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            ws.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{host.Port}");
            await ws.ConnectAsync(BridgeUri(host, token: BridgeToken(host)), cts.Token);

            var payload = new byte[] { 1, 2, 3, 4, 5 };
            await ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary, true, cts.Token);

            var recv = new byte[16];
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(recv), cts.Token);
            Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
            Assert.Equal(payload, recv[..result.Count]);
        }
        finally
        {
            try { tcp.Stop(); } catch { /* ignore */ }
            await Task.WhenAny(echo, Task.Delay(1000));
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    private static string BridgeToken(VncWebHost host)
        => FragmentParameter(host.PageUrl(), "token");

    private static Uri BridgeUri(VncWebHost host, string? token)
    {
        var url = $"ws://127.0.0.1:{host.Port}/websockify";
        if (token is not null)
            url += "?token=" + Uri.EscapeDataString(token);
        return new Uri(url);
    }

    private static string FragmentParameter(string url, string name)
    {
        var fragment = new Uri(url).Fragment.TrimStart('#');
        foreach (var part in fragment.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = separator < 0 ? part : part[..separator];
            if (string.Equals(key, name, StringComparison.Ordinal))
                return separator < 0 ? string.Empty : Uri.UnescapeDataString(part[(separator + 1)..]);
        }
        return string.Empty;
    }
}
