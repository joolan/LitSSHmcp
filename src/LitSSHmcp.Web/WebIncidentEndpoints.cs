using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Web;

namespace LitSSHmcp.Web;

/// <summary>管理员事件接口：事件列表与 supersede。</summary>
public static class WebIncidentEndpoints
{
    public static void MapWebIncidentEndpoints(this WebApplication app)
    {
        var incidents = app.MapGroup("/api/admin/incidents");

        incidents.MapGet("/", async (HttpContext ctx, WebAccessLog log, IWebMemoryStoreFactory factory,
            string? assetId, string? status, int? limit) =>
        {
            var principal = RequireAdmin(ctx, log);
            if (principal is null) return Results.Empty;

            var store = factory.Create(principal.UserId.ToString(), isAdmin: true);
            var records = await store.ListIncidentsAsync(assetId?.Trim(), status?.Trim(), limit ?? 50,
                ctx.RequestAborted);
            return Results.Ok(new
            {
                items = records.Select(i => new
                {
                    id = i.Id,
                    title = i.Title,
                    symptom = i.Symptom,
                    assetIds = i.AssetIds,
                    rootCause = i.RootCause,
                    actions = i.Actions,
                    outcome = i.Outcome,
                    evidence = i.Evidence,
                    confidence = i.Confidence,
                    status = i.Status,
                    createdAt = i.CreatedAt,
                    updatedAt = i.UpdatedAt,
                    supersededBy = i.SupersededBy
                })
            });
        });

        incidents.MapPost("/{id:long}/supersede", async (long id, SupersedeIncidentRequest? req,
            HttpContext ctx, WebAccessLog log, IWebMemoryStoreFactory factory) =>
        {
            var principal = RequireAdmin(ctx, log);
            if (principal is null) return Results.Empty;
            if (req?.NewId is not > 0)
                return Results.BadRequest(new { error = "newId 必须为正整数" });

            var store = factory.Create(principal.UserId.ToString(), isAdmin: true);
            var ok = await store.SupersedeIncidentAsync(id, req.NewId.Value, ctx.RequestAborted);
            var message = ok
                ? $"事件 #{id} 已被标记 superseded → #{req.NewId}"
                : $"事件 #{id} 不存在、已过时或无权限修改";
            log.Write(ok ? "incident_supersede" : "incident_supersede_failed", principal.Username,
                ctx.Connection.RemoteIpAddress?.ToString(), $"old={id} new={req.NewId}");
            return ok ? Results.Ok(new { status = "superseded", message })
                : Results.BadRequest(new { error = message });
        });
    }

    private static WebPrincipal? RequireAdmin(HttpContext ctx, WebAccessLog log)
    {
        var principal = ctx.GetWebPrincipal();
        if (principal is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return null;
        }
        if (string.Equals(principal.Role, HostEnv.AdminRole, StringComparison.OrdinalIgnoreCase))
            return principal;

        log.Write("forbidden", principal.Username, ctx.Connection.RemoteIpAddress?.ToString(),
            ctx.Request.Path);
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return null;
    }
}

public sealed record SupersedeIncidentRequest(long? NewId);
