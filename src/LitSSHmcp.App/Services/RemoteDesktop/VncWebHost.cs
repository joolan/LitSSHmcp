using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using LitSSHmcp.Core.Services.Security;

namespace LitSSHmcp.App.Services.RemoteDesktop;

/// <summary>
/// 为内嵌 VNC 视图提供本机回环服务：
/// ① 静态托管随包分发的 <b>noVNC</b> 资源（<c>novnc/</c>，默认页面 <c>vnc_lite.html</c>）；
/// ② 在 <c>/websockify</c> 上把 WebSocket 桥接到目标 VNC 的 TCP 端点（可为经 SSH 隧道后的 127.0.0.1 本地端口）。
/// 之所以用本机 HTTP 提供页面（而非 https 虚拟主机映射），是为了让页面走 <c>http://</c>，从而可用明文 <c>ws://</c>，
/// 避免 Chromium 对安全上下文（https）的混合内容拦截。
/// </summary>
public sealed class VncWebHost : IDisposable
{
    private readonly string _rootDir;
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly string _bridgeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly object _gate = new();

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _port;
    private int _disposed;

    public VncWebHost(string rootDir, string targetHost, int targetPort)
    {
        _rootDir = rootDir ?? throw new ArgumentNullException(nameof(rootDir));
        _targetHost = targetHost;
        _targetPort = targetPort;
    }

    /// <summary>监听端口（<see cref="Start"/> 成功后有值）。</summary>
    public int Port => _port;

    /// <summary>
    /// noVNC 页面地址（供 WebView2 导航）。敏感参数放 fragment：不会进入 HTTP 请求/Referer/服务端日志，
    /// 页面脚本读取后立即 <c>history.replaceState</c> 清除；WS 握手使用随机桥接 token。
    /// </summary>
    public string PageUrl(string? password = null)
    {
        var url = $"http://127.0.0.1:{_port}/vnc_lite.html?host=127.0.0.1&port={_port}&path=websockify&scale=true";
        var fragment = "token=" + Uri.EscapeDataString(_bridgeToken);
        if (!string.IsNullOrEmpty(password))
            fragment += "&password=" + Uri.EscapeDataString(password);
        return url + "#" + fragment;
    }

    /// <summary>启动服务（尝试若干端口）。失败返回 false。</summary>
    public bool Start()
    {
        if (_disposed != 0)
            return false;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var port = GetFreeLoopbackPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch
            {
                try { listener.Close(); } catch { /* ignore */ }
                continue;
            }

            lock (_gate)
            {
                _listener = listener;
                _port = port;
                _cts = new CancellationTokenSource();
            }
            _ = Task.Run(() => AcceptLoopAsync(listener, _cts!.Token));
            return true;
        }
        return false;
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                break;   // 监听停止 / 释放
            }
            _ = Task.Run(() => HandleAsync(ctx, ct));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (ctx.Request.IsWebSocketRequest && path.TrimEnd('/').Equals("/websockify", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsBridgeRequestAuthorized(ctx))
                {
                    ctx.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    ctx.Response.Close();
                    return;
                }
                await BridgeAsync(ctx, ct).ConfigureAwait(false);
                return;
            }
            await ServeStaticAsync(ctx).ConfigureAwait(false);
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { /* ignore */ }
        }
    }

    // ---- WebSocket ←→ TCP 桥接 ----

    /// <summary>
    /// 桥接握手授权：只接受本页面发起的同源 WS（Origin/Host 必须是当前回环端口），
    /// 并校验每实例随机 token，防止恶意网页或本机其他进程借用桥接访问 VNC。
    /// </summary>
    private bool IsBridgeRequestAuthorized(HttpListenerContext ctx)
    {
        var expectedAuthority = $"127.0.0.1:{_port}";
        var origin = ctx.Request.Headers["Origin"];
        var host = ctx.Request.Headers["Host"];
        if (!string.Equals(origin, "http://" + expectedAuthority, StringComparison.Ordinal)
            || !string.Equals(host, expectedAuthority, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = ctx.Request.QueryString["token"];
        return FixedTimeEquals(token, _bridgeToken);
    }

    private static bool FixedTimeEquals(string? left, string right)
    {
        if (left is null)
            return false;
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
               && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private async Task BridgeAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        WebSocket? ws = null;
        TcpClient? tcp = null;
        try
        {
            ws = (await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false)).WebSocket;
            tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(_targetHost, _targetPort, ct).ConfigureAwait(false);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var stream = tcp.GetStream();
            var wsToTcp = PumpWsToTcpAsync(ws, stream, linked.Token);
            var tcpToWs = PumpTcpToWsAsync(ws, stream, linked.Token);

            await Task.WhenAny(wsToTcp, tcpToWs).ConfigureAwait(false);
            linked.Cancel();
            await Task.WhenAll(SafeAwait(wsToTcp), SafeAwait(tcpToWs)).ConfigureAwait(false);
        }
        catch
        {
            // 连接失败/中断：忽略（noVNC 会显示连接错误）
        }
        finally
        {
            try { if (ws is not null && ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
            try { ws?.Dispose(); } catch { /* ignore */ }
            try { tcp?.Dispose(); } catch { /* ignore */ }
        }
    }

    private static async Task SafeAwait(Task t)
    {
        try { await t.ConfigureAwait(false); } catch { /* ignore */ }
    }

    private static async Task PumpWsToTcpAsync(WebSocket ws, NetworkStream tcp, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            if (result.Count > 0)
            {
                try
                {
                    await tcp.WriteAsync(buffer.AsMemory(0, result.Count), ct).ConfigureAwait(false);
                    await tcp.FlushAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
        }
    }

    private static async Task PumpTcpToWsAsync(WebSocket ws, NetworkStream tcp, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            int read;
            try
            {
                read = await tcp.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            if (read <= 0)
                break;

            try
            {
                await ws.SendAsync(new ArraySegment<byte>(buffer, 0, read), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
        }
    }

    // ---- 静态资源 ----

    private async Task ServeStaticAsync(HttpListenerContext ctx)
    {
        var rel = (ctx.Request.Url?.AbsolutePath ?? "/").TrimStart('/');
        if (string.IsNullOrEmpty(rel))
            rel = "vnc_lite.html";

        var rootFull = Path.GetFullPath(_rootDir);
        var fileFull = Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));

        // 防目录穿越：按“根 + 分隔符”边界并解析链接，避免 novnc-evil 之类同前缀目录绕过。
        if (rel.IndexOf('\0') >= 0 || !PathGuard.IsUnder(rootFull, fileFull) || !File.Exists(fileFull))
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            ctx.Response.Close();
            return;
        }

        var bytes = await File.ReadAllBytesAsync(fileFull).ConfigureAwait(false);
        ctx.Response.StatusCode = (int)HttpStatusCode.OK;
        ctx.Response.ContentType = ContentTypeFor(fileFull);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".png" => "image/png",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream"
    };

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        try { _listener?.Close(); } catch { /* ignore */ }
        _listener = null;
        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;
    }

    /// <summary>随包分发的 noVNC 资源目录。</summary>
    public static string DefaultRootDir => Path.Combine(AppContext.BaseDirectory, "novnc");
}
