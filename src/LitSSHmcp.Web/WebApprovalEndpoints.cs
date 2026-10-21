using System.Net.WebSockets;
using System.Text.Json;
using LitSSHmcp.Web.Approvals;

namespace LitSSHmcp.Web;

/// <summary>POST /api/approvals/{id} 请求体。</summary>
public sealed record ApprovalDecisionRequest(bool? Approved);

/// <summary>
/// 审批 REST + WebSocket 推送端点（报告 §3.3.1/§3.2）：
/// GET /api/approvals 本用户待决快照；POST /api/approvals/{id} 决策（CSRF 双提交，写 cli 文件通道 Channel="web"）；
/// /ws/approvals cookie 认证 + Origin 校验 + 注册推送，S→C 帧 approval_snapshot/approval_request/approval_closed，
/// C→S 仅接受 ping（决策走 REST，吃 CSRF 防护；客户端按 id 幂等 upsert）。
/// </summary>
public static class WebApprovalEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>单帧发送超时：慢客户端/半开连接不得无限占用推送泵。</summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    public static void MapWebApprovalEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/approvals");

        api.MapGet("/", (HttpContext ctx, WebApprovalHub hub) =>
        {
            var principal = ctx.GetWebPrincipal();
            if (principal is null) return Results.Unauthorized();
            return Results.Ok(new { items = hub.SnapshotFor(principal.UserId.ToString()) });
        });

        api.MapPost("/{id}", (string id, ApprovalDecisionRequest? req, HttpContext ctx, WebApprovalHub hub) =>
        {
            var principal = ctx.GetWebPrincipal();
            if (principal is null) return Results.Unauthorized();
            if (req?.Approved is null) return Results.BadRequest(new { error = "approved 必须为布尔值" });

            var result = hub.Decide(principal.UserId.ToString(), id, req.Approved.Value,
                principal.Username, ctx.Connection.RemoteIpAddress?.ToString());
            if (result == ApprovalDecideResult.NotFound)
                return Results.NotFound(new { error = "审批请求不存在" });
            return Results.Ok(new { status = "decided", approved = req.Approved.Value });
        });

        app.Map("/ws/approvals", HandleWebSocketAsync);
    }

    private static async Task HandleWebSocketAsync(HttpContext ctx, WebApprovalHub hub, WebAccessLog accessLog,
        WebRateLimitPolicy ratePolicy, WebRateLimitStore rateStore, WebSocketConnectionLimiter connections)
    {
        var principal = ctx.GetWebPrincipal();
        if (principal is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        // WS 握手限速：WS 升级后不再经过 HTTP 限速中间件，必须在端点内单独计数。
        if (!rateStore.TryAcquire(ip, "ws", ratePolicy.WsHandshakesPerMinute, DateTimeOffset.UtcNow))
        {
            accessLog.Write("ws_rate_limited", principal.Username, ip, "ws_handshake");
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }
        // Origin 必须存在且 scheme/host/port 与当前请求完全一致，防跨站 WebSocket 劫持。
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!WebSocketOriginPolicy.IsAllowed(ctx, origin))
        {
            accessLog.Write("ws_origin_rejected", principal.Username, ip, origin);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // 每用户 + 每 IP 并发连接上限；租约在连接方法返回时自动释放。
        using var userLease = connections.TryAcquire($"user:{principal.UserId}", ratePolicy.WsMaxConnectionsPerUser);
        using var ipLease = connections.TryAcquire($"ip:{ip}", ratePolicy.WsMaxConnectionsPerIp);
        if (userLease is null || ipLease is null)
        {
            accessLog.Write("ws_connection_limited", principal.Username, ip, "ws_approvals");
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        accessLog.Write("ws_approvals_connect", principal.Username, ip);

        // 注册即入队快照（闸门内原子），此后 Hub 增量推送经同一队列保序流出
        var client = hub.Register(principal.UserId.ToString());
        var sendGate = new SemaphoreSlim(1, 1);
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in client.Frames.ReadAllAsync(connectionCts.Token))
                {
                    if (!await SendWithTimeoutAsync(socket, sendGate, frame, connectionCts.Token))
                    {
                        connectionCts.Cancel();
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 请求/连接已取消
            }
        }, CancellationToken.None);

        try
        {
            while (socket.State == WebSocketState.Open && !connectionCts.IsCancellationRequested)
            {
                string? frame;
                try
                {
                    frame = await WebSessionEndpoints.ReceiveTextAsync(socket, connectionCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                if (frame is null)
                {
                    // 对端发起关闭：回执 close 帧完成握手
                    if (socket.State == WebSocketState.CloseReceived)
                    {
                        try
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                        }
                        catch
                        {
                            // 回执失败（对端已断开）忽略
                        }
                    }
                    break;
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(frame);
                }
                catch (JsonException)
                {
                    if (!await SendWithTimeoutAsync(socket, sendGate,
                            new { type = "error", message = "非法 JSON 帧" }, connectionCts.Token))
                        break;
                    continue;
                }

                using (doc)
                {
                    var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (string.Equals(type, "ping", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!await SendWithTimeoutAsync(socket, sendGate,
                                new { type = "pong" }, connectionCts.Token))
                            break;
                        continue;
                    }
                    if (!await SendWithTimeoutAsync(socket, sendGate,
                            new { type = "error", message = "未知帧类型" }, connectionCts.Token))
                        break;
                }
            }
        }
        finally
        {
            connectionCts.Cancel();
            hub.Unregister(client);
            accessLog.Write("ws_approvals_disconnect", principal.Username, ip);
            try { await pump; } catch { /* 泵随连接退出 */ }
        }
    }

    private static async Task<bool> SendWithTimeoutAsync(WebSocket socket, SemaphoreSlim gate,
        object frame, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SendTimeout);
        return await WebSessionEndpoints.SendJsonAsync(socket, gate, frame, timeout.Token);
    }
}
