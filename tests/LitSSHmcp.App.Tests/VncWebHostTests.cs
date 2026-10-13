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

            // 不存在的文件 → 404
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/missing.txt")).StatusCode);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Page_url_points_at_websockify_and_encodes_password()
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
            Assert.Contains("password=p%40ss%20w%2Frd", url);   // 已 URL 转义
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
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{host.Port}/websockify"), cts.Token);

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
}
