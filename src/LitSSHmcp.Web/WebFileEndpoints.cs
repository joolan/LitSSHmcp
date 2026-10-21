using System.Buffers;
using System.Globalization;
using LitSSHmcp.Core.Services.Web;
using LitSSHmcp.Web.Sessions;
using Microsoft.Net.Http.Headers;

namespace LitSSHmcp.Web;

/// <summary>
/// 会话目录文件浏览/下载端点（评估报告 D6/§3.1/§10.C.2-3 交付物取回）：
/// GET /api/sessions/{id}/files?path= 目录条目（非递归，省略 path = 会话根）；
/// GET /api/sessions/{id}/download?path= 文件下载（Content-Disposition 附件 + 单段 Range）。
/// owner + 固定根 + PathGuard 物理包含校验在 WebSessionManager 内完成；
/// URL 由框架先解码再入参（解码后再校验），越权统一 404 并落 file_denied 访问日志，不泄露存在性。
/// 下载另设单文件上限（413）、每用户并发上限（429）与响应超时；流式发送，不整文件读入内存。
/// </summary>
public static class WebFileEndpoints
{
    private const int DownloadBufferSize = 64 * 1024;

    public static void MapWebFileEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/sessions");

        api.MapGet("/{id:long}/files", async (long id, string? path, HttpContext ctx,
            WebSessionManager manager, CancellationToken ct) =>
        {
            var principal = ctx.GetWebPrincipal();
            if (principal is null) return Results.Unauthorized();
            try
            {
                return Results.Ok(new { items = await manager.ListFilesAsync(principal, id, path, ct) });
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

        api.MapGet("/{id:long}/download", async (long id, string? path, HttpContext ctx,
            WebSessionManager manager, WebDownloadPolicy policy, WebDownloadLimiter limiter,
            WebAccessLog accessLog, CancellationToken ct) =>
        {
            var principal = ctx.GetWebPrincipal();
            if (principal is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path 必填" });

            var ip = ctx.Connection.RemoteIpAddress?.ToString();
            using var lease = limiter.TryAcquire($"user:{principal.UserId}", policy.MaxConcurrentPerUser);
            if (lease is null)
            {
                accessLog.Write("file_download_limited", principal.Username, ip,
                    $"session={id} limit={policy.MaxConcurrentPerUser}");
                return Results.Json(new { error = $"并发下载数已达上限（{policy.MaxConcurrentPerUser}）" },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            WebFileDownload? download;
            try
            {
                download = await manager.OpenDownloadAsync(principal, id, path, policy.MaxBytes, ct);
            }
            catch (WebSessionNotFoundException)
            {
                return Results.NotFound();
            }
            catch (WebFilePathDeniedException)
            {
                return Results.NotFound();
            }
            catch (WebFileTooLargeException ex)
            {
                accessLog.Write("file_download_too_large", principal.Username, ip,
                    $"session={id} size={ex.Size} limit={ex.Limit}");
                return Results.Json(new
                {
                    error = $"文件超过 Web 下载上限（{ex.Limit} 字节）",
                    size = ex.Size,
                    limit = ex.Limit
                }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            if (download is null)
                return Results.NotFound();

            await using (download.Content)
            {
                return await StreamDownloadAsync(ctx, download, policy.Timeout, accessLog,
                    principal, id, ip, ct);
            }
        });
    }

    private static async Task<IResult> StreamDownloadAsync(HttpContext ctx, WebFileDownload download,
        TimeSpan timeout, WebAccessLog accessLog, WebPrincipal principal, long sessionId, string? ip,
        CancellationToken requestAborted)
    {
        var length = download.Length;
        long start = 0;
        var count = length;
        var rangeHeader = ctx.Request.Headers["Range"].ToString();
        var hasRange = !string.IsNullOrWhiteSpace(rangeHeader);

        if (hasRange)
        {
            if (!TryParseSingleRange(rangeHeader, length, out start, out var end))
            {
                ctx.Response.Headers["Content-Range"] = $"bytes */{length}";
                return Results.Json(new { error = "Range 无效，仅支持单段字节范围", size = length },
                    statusCode: StatusCodes.Status416RangeNotSatisfiable);
            }

            count = end - start + 1;
            ctx.Response.StatusCode = StatusCodes.Status206PartialContent;
            ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{length}";
        }

        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength = count;
        ctx.Response.Headers["Accept-Ranges"] = "bytes";
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(download.FileName);
        ctx.Response.Headers["Content-Disposition"] = disposition.ToString();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        if (timeout > TimeSpan.Zero)
            timeoutCts.CancelAfter(timeout);

        var buffer = ArrayPool<byte>.Shared.Rent(DownloadBufferSize);
        try
        {
            download.Content.Seek(start, SeekOrigin.Begin);
            var remaining = count;
            while (remaining > 0)
            {
                var read = await download.Content.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), timeoutCts.Token);
                if (read <= 0)
                    break;
                await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), timeoutCts.Token);
                remaining -= read;
            }
            await ctx.Response.Body.FlushAsync(timeoutCts.Token);
            return Results.Empty;
        }
        catch (OperationCanceledException)
        {
            if (!requestAborted.IsCancellationRequested)
            {
                accessLog.Write("file_download_timeout", principal.Username, ip,
                    $"session={sessionId} file={download.FileName}");
                ctx.Abort();
            }
            return Results.Empty;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>解析单段 <c>bytes=start-end</c> / <c>bytes=start-</c> / <c>bytes=-suffix</c>；多段/越界拒绝。</summary>
    private static bool TryParseSingleRange(string? header, long length, out long start, out long end)
    {
        start = 0;
        end = 0;
        if (length <= 0 || string.IsNullOrWhiteSpace(header))
            return false;

        var value = header.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return false;

        var spec = value[6..].Trim();
        if (spec.Length == 0 || spec.Contains(','))
            return false;

        var dash = spec.IndexOf('-');
        if (dash < 0 || spec.IndexOf('-', dash + 1) >= 0)
            return false;

        var startText = spec[..dash].Trim();
        var endText = spec[(dash + 1)..].Trim();

        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix)
                || suffix <= 0)
                return false;
            if (suffix > length)
                suffix = length;
            start = length - suffix;
            end = length - 1;
            return true;
        }

        if (!long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out start)
            || start < 0 || start >= length)
            return false;

        if (endText.Length == 0)
        {
            end = length - 1;
            return true;
        }

        if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out end)
            || end < start)
            return false;

        if (end >= length)
            end = length - 1;
        return true;
    }
}
