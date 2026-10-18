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
            try
            {
                var info = await manager.CreateSessionAsync(principal, req.Title, ctx.RequestAborted);
                return Results.Ok(info);
            }
            catch (WebSessionLimitException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        api.MapGet("/", async (HttpContext ctx, WebSessionManager manager) =>
        {
            var principal = ctx.GetWebPrincipal();
            if (principal is null) return Results.Unauthorized();
            return Results.Ok(await manager.ListSessionsAsync(principal, ctx.RequestAborted));
        });

        // 消息分页：默认最近 120 条；beforeId 向前翻页（长会话懒加载）
        api.MapGet("/{id:long}/messages", async (long id, long? beforeId, int? limit, HttpContext ctx, WebSessionManager manager) =>
        {
            var principal = ctx.GetWebPrincipal();
            if (principal is null) return Results.Unauthorized();
            try
            {
                return Results.Ok(await manager.GetMessagePageAsync(principal, id, beforeId, limit, ctx.RequestAborted));
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

        // 聊天图片附件上传（仅图片、单张 ≤10MB）：返回相对会话目录路径，随 WS user 帧回传
        api.MapPost("/{id:long}/attachments", async (long id, HttpContext ctx, WebSessionManager manager) =>
        {
            var principal = ctx.GetWebPrincipal();
            if (principal is null) return Results.Unauthorized();
            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { error = "需要 multipart/form-data" });

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "缺少文件" });

            try
            {
                await using var stream = file.OpenReadStream();
                var path = await manager.SaveAttachmentAsync(principal, id, file.FileName, file.ContentType, stream, ctx.RequestAborted);
                return Results.Ok(new
                {
                    path,
                    name = file.FileName,
                    contentType = file.ContentType,
                    size = file.Length
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (WebSessionNotFoundException)
            {
                return Results.NotFound();
            }
            catch (WebFilePathDeniedException)
            {
                return Results.NotFound();
            }
        });

        // 会话级模型切换（下一轮生效；运行中 409）
        api.MapPut("/{id:long}/provider", async (long id, SetProviderRequest? req, HttpContext ctx, WebSessionManager manager) =>
        {
            var principal = ctx.GetWebPrincipal();
            if (principal is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req?.ProviderId))
                return Results.BadRequest(new { error = "providerId 不能为空" });
            try
            {
                await manager.SetProviderAsync(principal, id, req.ProviderId!, ctx.RequestAborted);
                return Results.Ok(new { providerId = req.ProviderId });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (WebSessionNotFoundException)
            {
                return Results.NotFound();
            }
            catch (WebSessionBusyException)
            {
                return Results.Json(new { error = "会话正在执行，稍后再切换模型" }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        app.Map("/ws/sessions/{id:long}", HandleWebSocketAsync);
    }

    /// <summary>解析 WS user 帧里的图片附件（最多 5 张；路径由上传接口返回）。</summary>
    private static IReadOnlyList<WebAttachmentInput>? ParseAttachments(JsonElement root)
    {
        if (!root.TryGetProperty("attachments", out var array) || array.ValueKind != JsonValueKind.Array)
            return null;

        var items = new List<WebAttachmentInput>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;
            var path = element.TryGetProperty("path", out var p) ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(path))
                continue;
            var contentType = element.TryGetProperty("contentType", out var c) ? c.GetString() : null;
            var name = element.TryGetProperty("name", out var n) ? n.GetString() : null;
            items.Add(new WebAttachmentInput(path!, contentType, name));
            if (items.Count >= 5)
                break;
        }
        return items.Count == 0 ? null : items;
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
        Task? turnTask = null;
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
                    var attachments = ParseAttachments(doc.RootElement);

                    // 回合在后台执行：接收循环继续读帧——stop 能在回合进行中即时生效
                    // （旧实现 await 回合导致 stop 帧要等回合结束才被处理）。
                    if (turnTask is { IsCompleted: false })
                    {
                        await SendJsonAsync(socket, sendGate, new { type = "busy", message = "上一轮仍在进行中" }, ctx.RequestAborted);
                        continue;
                    }
                    turnTask = RunTurnAndStreamAsync(manager, principal, id, text!, attachments, socket, sendGate, ctx.RequestAborted);
                    continue;
                }
            }
        }
        finally
        {
            if (turnTask is not null)
            {
                try { await turnTask; } catch { /* 回合收尾异常不影响连接清理 */ }
            }
            accessLog.Write("ws_disconnect", principal.Username, ctx.Connection.RemoteIpAddress?.ToString(), $"session={id}");
        }
    }

    /// <summary>
    /// 后台执行一轮并流式推帧（与接收循环并行，stop 可即时取消）：事件经无界 Channel 保持顺序，
    /// 由泵串行发送；回合结束（done/error）后泵随之结束。
    /// </summary>
    private static async Task RunTurnAndStreamAsync(WebSessionManager manager, WebPrincipal principal, long id,
        string text, IReadOnlyList<WebAttachmentInput>? attachments, WebSocket socket, SemaphoreSlim sendGate, CancellationToken ct)
    {
        var frames = Channel.CreateUnbounded<object>();
        var pump = Task.Run(async () =>
        {
            await foreach (var frame in frames.Reader.ReadAllAsync())
                await SendJsonAsync(socket, sendGate, frame, CancellationToken.None);
        }, CancellationToken.None);
        var progress = new FrameProgress(frames.Writer);
        try
        {
            await manager.RunTurnAsync(principal, id, text, attachments, progress, ct);
            await frames.Writer.WriteAsync(new { type = "done" }, CancellationToken.None);
        }
        catch (WebSessionBusyException)
        {
            await frames.Writer.WriteAsync(new { type = "busy", message = "上一轮仍在进行中" }, CancellationToken.None);
        }
        catch (WebSessionNotFoundException)
        {
            await frames.Writer.WriteAsync(new { type = "error", message = "会话不存在" }, CancellationToken.None);
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

/// <summary>PUT /api/sessions/{id}/provider 请求体（会话级模型切换）。</summary>
public sealed record SetProviderRequest(string? ProviderId);
}
