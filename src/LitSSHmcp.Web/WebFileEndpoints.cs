using LitSSHmcp.Web.Sessions;

namespace LitSSHmcp.Web;

/// <summary>
/// 会话目录文件浏览/下载端点（评估报告 D6/§3.1/§10.C.2-3 交付物取回）：
/// GET /api/sessions/{id}/files?path= 目录条目（非递归，省略 path = 会话根）；
/// GET /api/sessions/{id}/download?path= 文件下载（Content-Disposition 附件）。
/// owner + 固定根 + PathGuard 物理包含校验在 WebSessionManager 内完成；
/// URL 由框架先解码再入参（解码后再校验），越权统一 404 并落 file_denied 访问日志，不泄露存在性。
/// </summary>
public static class WebFileEndpoints
{
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
            WebSessionManager manager, CancellationToken ct) =>
        {
            var principal = ctx.GetWebPrincipal();
            if (principal is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path 必填" });
            try
            {
                var download = await manager.OpenDownloadAsync(principal, id, path, ct);
                return download is null
                    ? Results.NotFound()
                    : Results.File(download.Content, "application/octet-stream", download.FileName);
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
    }
}
