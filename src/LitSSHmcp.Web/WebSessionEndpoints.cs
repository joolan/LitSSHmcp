using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LitSSHmcp.Agent;
using LitSSHmcp.Core.Services.Web;
using LitSSHmcp.Web.Sessions;

namespace LitSSHmcp.Web;

/// <summary>会话 REST + WebSocket 流式聊天端点（评估报告 D4/§3.2）。</summary>
public static class WebSessionEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void MapWebSessionEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/sessions");

        api.MapPost("/", async (CreateSessionRequest req, HttpContext ctx, WebSessionManager manager) =>
        {
            var principal = ctx.GetWebPrincipal();
            if (principal is null) return Results.Unauthorized();
            var info = await manager.CreateSessionAsync(principal, req.Title, ctx.RequestAborted);
            return Results.Ok(info);
        });

        api.MapGet("/", async (HttpContext ctx, WebSessionManager manager) =>
        {
            var principal = ctx.GetWebPrincipal();
            if (principal is null) return Results.Unauthorized();
            return Results.Ok(await manager.ListSessionsAsync(principal, ctx.RequestAborted));
        });

        api.MapGet("/{id:long}/messages", async (long id, HttpContext ctx, WebSessionManager manager) =>
        {
            var principal = ctx.GetWebPrincipal();
            if (principal is null) return Results.Unauthorized();
            try
            {
                return Results.Ok(await manager.GetMessagesAsync(principal, id, ctx.RequestAborted));
            }
            catch (WebSessionNotFoundException)
            {
                return Results.NotFound();
            }
        });

        api.MapDelete("/{id:long}", async (long id, HttpContext ctx, WebSessionManager manager) =>
        {
            var principal = ctx.GetWebPrincipal();
            if (principal is null) return Results.Unauthorized();
            try
            {
                await manager.DeleteSessionAsync(principal, id, ctx.RequestAborted);
                return Results.NoContent();
            }
            catch (WebSessionNotFoundException)
            {
                return Results.NotFound();
            }
        });

        api.MapPost("/{id:long}/stop", async (long id, HttpContext ctx, WebSessionManager manager) =>
        {
            var principal = ctx.GetWebPrincipal();
            if (principal is null) return Results.Unauthorized();
            try
            {
                await manager.StopTurnAsync(principal, id, ctx.RequestAborted);
                return Results.NoContent();
            }
            catch (WebSessionNotFoundException)
            {
                return Results.NotFound();
            }
        });

        app.Map("/ws/sessions/{id:long}", HandleWebSocketAsync);
    }

    /// <summary>
    /// WebSocket 流式聊天：cookie 认证（握手经 WebAuthMiddleware）+ WS Origin 校验（§10.B.1）+
    /// 每消息 owner 校验（§10.B.5，经 manager）。协议（JSON 文本帧）：
    /// C→S <c>{"type":"user","text":…}</c> / <c>{"type":"stop"}</c>；
    /// S→C <c>assistant_text/tool/done/error/busy</c> 帧。
    /// </summary>
    private static async Task HandleWebSocketAsync(HttpContext ctx, long id, WebSessionManager manager, WebAccessLog accessLog)
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
        // WS Origin 校验（§10.B.1）：带 Origin 的浏览器握手必须与 Host 一致，防跨站 WebSocket 劫持
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) &&
            (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) ||
             !string.Equals(originUri.Host, ctx.Request.Host.Host, StringComparison.OrdinalIgnoreCase)))
        {
            accessLog.Write("ws_origin_rejected", principal.Username, ctx.Connection.RemoteIpAddress?.ToString(), origin);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        // owner 预检（§10.B.5 会话隔离；每消息仍经 manager 复检）：非本用户统一 404 不泄露存在性
        if (!await manager.IsOwnerAsync(principal, id, ctx.RequestAborted))
        {
            accessLog.Write("ws_denied", principal.Username, ctx.Connection.RemoteIpAddress?.ToString(), $"session={id}");
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        accessLog.Write("ws_connect", principal.Username, ctx.Connection.RemoteIpAddress?.ToString(), $"session={id}");

        var sendGate = new SemaphoreSlim(1, 1);
        try
        {
            while (socket.State == WebSocketState.Open && !ctx.RequestAborted.IsCancellationRequested)
            {
                string? frame;
                try
                {
                    frame = await ReceiveTextAsync(socket, ctx.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                if (frame is null)
                {
                    // 对端发起关闭：回执 close 帧完成握手（否则客户端 CloseAsync 挂起/报错）
                    if (socket.State == WebSocketState.CloseReceived)
                    {
                        try
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                        }
                        catch
                        {
                            // 关闭回执失败（对端已断开）忽略
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
                    await SendJsonAsync(socket, sendGate, new { type = "error", message = "非法 JSON 帧" }, ctx.RequestAborted);
                    continue;
                }

                using (doc)
                {
                    var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (string.Equals(type, "stop", StringComparison.OrdinalIgnoreCase))
                    {
                        try { await manager.StopTurnAsync(principal, id, ctx.RequestAborted); }
                        catch (WebSessionNotFoundException) { await SendCloseAsync(socket, 4404, "会话不存在"); break; }
                        continue;
                    }
                    if (!string.Equals(type, "user", StringComparison.OrdinalIgnoreCase))
                    {
                        await SendJsonAsync(socket, sendGate, new { type = "error", message = "未知帧类型" }, ctx.RequestAborted);
                        continue;
                    }
                    var text = doc.RootElement.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        await SendJsonAsync(socket, sendGate, new { type = "error", message = "消息不能为空" }, ctx.RequestAborted);
                        continue;
                    }

                    // 有序发送泵：事件经无界 Channel 保持到达顺序地推给对端（Progress 线程池派发会乱序）
                    var frames = Channel.CreateUnbounded<object>();
                    var pump = Task.Run(async () =>
                    {
                        await foreach (var frame in frames.Reader.ReadAllAsync())
                            await SendJsonAsync(socket, sendGate, frame, CancellationToken.None);
                    }, CancellationToken.None);
                    var progress = new FrameProgress(frames.Writer);
                    try
                    {
                        await manager.RunTurnAsync(principal, id, text!, progress, ctx.RequestAborted);
                        await frames.Writer.WriteAsync(new { type = "done" }, CancellationToken.None);
                    }
                    catch (WebSessionBusyException)
                    {
                        await frames.Writer.WriteAsync(new { type = "busy", message = "上一轮仍在进行中" }, CancellationToken.None);
                    }
                    catch (WebSessionNotFoundException)
                    {
                        frames.Writer.TryComplete();
                        await pump;
                        await SendCloseAsync(socket, 4404, "会话不存在");
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        await frames.Writer.WriteAsync(new { type = "done", stopped = true }, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        await frames.Writer.WriteAsync(new { type = "error", message = ex.Message }, CancellationToken.None);
                    }
                    finally
                    {
                        frames.Writer.TryComplete();
                        await pump;
                    }
                }
            }
        }
        finally
        {
            accessLog.Write("ws_disconnect", principal.Username, ctx.Connection.RemoteIpAddress?.ToString(), $"session={id}");
        }
    }

    internal static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType != WebSocketMessageType.Text)
                return null;
            ms.Write(buffer, 0, result.Count);
            if (ms.Length > 1_000_000)
                throw new InvalidOperationException("消息帧过大");
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }
    }

    /// <summary>事件 → 协议帧的同步转换器（Report 即入队，保持顺序；帧序列化在泵内完成）。</summary>
    private sealed class FrameProgress(ChannelWriter<object> writer) : IProgress<AgentEvent>
    {
        public void Report(AgentEvent e)
        {
            object frame = e.Kind switch
            {
                AgentEventKind.AssistantText => new { type = "assistant_text", text = e.Text },
                AgentEventKind.AssistantReasoning => new { type = "reasoning", text = e.Text },
                AgentEventKind.ToolCall => new { type = "tool", tool = e.ToolName, args = e.ArgumentsJson },
                AgentEventKind.ToolResult => new { type = "tool_result", tool = e.ToolName, ok = e.ToolSuccess, ms = e.DurationMs },
                AgentEventKind.MemoryRecall => new { type = "memory_recall", text = e.Text },
                AgentEventKind.Error => new { type = "error", message = e.Text },
                _ => new { type = "event", kind = e.Kind.ToString() }
            };
            writer.TryWrite(frame);
        }
    }

    internal static async Task SendJsonAsync(WebSocket socket, SemaphoreSlim gate, object frame, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
            return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame, JsonOpts));
        await gate.WaitAsync(ct);
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        catch
        {
            // 发送失败（对端已断开）忽略，读循环会自然退出
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task SendCloseAsync(WebSocket socket, int code, string reason)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync((WebSocketCloseStatus)code, reason, CancellationToken.None);
        }
        catch
        {
            // 关闭失败忽略
        }
    }

    public sealed record CreateSessionRequest(string? Title);
}
