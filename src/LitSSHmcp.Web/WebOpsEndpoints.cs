using LitSSHmcp.Agent;
using LitSSHmcp.Agent.Sessions;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Web;

namespace LitSSHmcp.Web;

/// <summary>运维视图（登录用户）：事件浏览、事件/会话时间线、资产画像。按当前用户团队/私有作用域读取。</summary>
public static class WebOpsEndpoints
{
    public static void MapWebOpsEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/ops");

        api.MapGet("/incidents", async (HttpContext ctx, WebAccessLog log, IWebMemoryStoreFactory factory,
            string? assetId, string? status, int? limit) =>
        {
            var principal = RequireUser(ctx, log);
            if (principal is null) return Results.Empty;

            var store = factory.Create(principal.UserId.ToString(), IsAdmin(principal));
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
                    updatedAt = i.UpdatedAt,
                    supersededBy = i.SupersededBy
                })
            });
        });

        api.MapGet("/timeline", async (HttpContext ctx, WebAccessLog log, IWebMemoryStoreFactory factory,
            IContextStore context, long? incidentId, string? sessionId, int? limit) =>
        {
            var principal = RequireUser(ctx, log);
            if (principal is null) return Results.Empty;

            var store = factory.Create(principal.UserId.ToString(), IsAdmin(principal));
            var tool = TimelineTools.Create(store, context);
            var text = await tool.InvokeAsync(new Dictionary<string, object?>
            {
                ["incidentId"] = incidentId,
                ["sessionId"] = sessionId,
                ["limit"] = limit ?? 200
            }, ctx.RequestAborted);
            return Results.Ok(new { text = text?.ToString() ?? string.Empty });
        });

        api.MapGet("/asset-profile", async (HttpContext ctx, WebAccessLog log, IWebMemoryStoreFactory factory,
            IConfigService configService, string? assetId, int? limit) =>
        {
            var principal = RequireUser(ctx, log);
            if (principal is null) return Results.Empty;
            if (string.IsNullOrWhiteSpace(assetId))
                return Results.BadRequest(new { error = "assetId 为必填" });

            var store = factory.Create(principal.UserId.ToString(), IsAdmin(principal));
            var text = await AssetProfileService.BuildAsync(store, configService, assetId.Trim(),
                limit ?? 10, ctx.RequestAborted);
            return Results.Ok(new { text });
        });
    }

    private static bool IsAdmin(WebPrincipal principal) =>
        string.Equals(principal.Role, HostEnv.AdminRole, StringComparison.OrdinalIgnoreCase);

    private static WebPrincipal? RequireUser(HttpContext ctx, WebAccessLog log)
    {
        var principal = ctx.GetWebPrincipal();
        if (principal is not null)
            return principal;
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return null;
    }
}
