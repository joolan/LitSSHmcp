using LitSSHmcp.Agent;
using LitSSHmcp.Core.Services.Security;

namespace LitSSHmcp.Web;

/// <summary>管理员记忆库接口（团队记忆历史/回滚/归档）。普通成员一律 403。</summary>
public static class WebMemoryEndpoints
{
    public static void MapWebMemoryEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/admin/memory");

        api.MapGet("/", async (HttpContext ctx, WebAccessLog log, IWebMemoryStoreFactory factory,
            string? kind, string? assetId, int? limit) =>
        {
            var principal = RequireAdmin(ctx, log);
            if (principal is null) return Results.Empty;

            var store = factory.Create(principal.UserId.ToString(), isAdmin: true);
            var records = await store.ListAsync(kind?.Trim(), assetId?.Trim(), limit ?? 100,
                includeRaw: true, ctx.RequestAborted);
            return Results.Ok(new
            {
                items = records.Select(r => new
                {
                    id = r.Id,
                    kind = r.Kind,
                    key = r.Key,
                    content = r.Content,
                    status = r.Status,
                    freq = r.Freq,
                    assetIds = r.AssetIds,
                    tags = r.Tags,
                    updatedAt = r.UpdatedAt
                })
            });
        });

        api.MapGet("/{id:long}/versions", async (long id, HttpContext ctx, WebAccessLog log,
            IWebMemoryStoreFactory factory) =>
        {
            var principal = RequireAdmin(ctx, log);
            if (principal is null) return Results.Empty;

            var store = factory.Create(principal.UserId.ToString(), isAdmin: true);
            var versions = await store.ListVersionsAsync(id, ctx.RequestAborted);
            return Results.Ok(new
            {
                items = versions.Select(v => new
                {
                    version = v.Version,
                    status = v.Status,
                    freq = v.Freq,
                    content = v.Content,
                    assetIds = v.AssetIds,
                    archivedAt = v.ArchivedAt
                })
            });
        });

        api.MapPost("/history/prune", async (PruneMemoryHistoryRequest? req, HttpContext ctx,
            WebAccessLog log, IWebMemoryStoreFactory factory) =>
        {
            var principal = RequireAdmin(ctx, log);
            if (principal is null) return Results.Empty;

            var keep = Math.Clamp(req?.Keep ?? AgentMemoryStore.DefaultHistoryKeep, 1, 1000);
            var store = factory.Create(principal.UserId.ToString(), isAdmin: true);
            var removed = await store.PruneHistoryAsync(keep, memoryId: null, ctx.RequestAborted);
            log.Write("memory_history_prune", principal.Username,
                ctx.Connection.RemoteIpAddress?.ToString(), $"keep={keep} removed={removed}");
            return Results.Ok(new { removed, keep });
        });

        api.MapPost("/{id:long}/rollback", async (long id, RollbackMemoryRequest? req, HttpContext ctx,
            WebAccessLog log, IWebMemoryStoreFactory factory) =>
        {
            var principal = RequireAdmin(ctx, log);
            if (principal is null) return Results.Empty;
            if (req?.Version is not > 0)
                return Results.BadRequest(new { error = "version 必须为正整数" });

            var store = factory.Create(principal.UserId.ToString(), isAdmin: true);
            var ok = await store.RestoreVersionAsync(id, req.Version.Value, ctx.RequestAborted);
            log.Write(ok ? "memory_rollback" : "memory_rollback_failed", principal.Username,
                ctx.Connection.RemoteIpAddress?.ToString(), $"id={id} version={req.Version}");
            return ok
                ? Results.Ok(new { status = "rolled_back", version = req.Version })
                : Results.NotFound(new { error = "版本不存在或无权回滚" });
        });

        api.MapPost("/{id:long}/archive", async (long id, HttpContext ctx, WebAccessLog log,
            IWebMemoryStoreFactory factory) =>
        {
            var principal = RequireAdmin(ctx, log);
            if (principal is null) return Results.Empty;

            var store = factory.Create(principal.UserId.ToString(), isAdmin: true);
            var ok = await store.ArchiveByIdAsync(id, ctx.RequestAborted);
            log.Write(ok ? "memory_archive" : "memory_archive_failed", principal.Username,
                ctx.Connection.RemoteIpAddress?.ToString(), $"id={id}");
            return ok
                ? Results.Ok(new { status = "superseded" })
                : Results.NotFound(new { error = "记忆不存在或无权归档" });
        });
    }

    private static LitSSHmcp.Core.Services.Web.WebPrincipal? RequireAdmin(HttpContext ctx, WebAccessLog log)
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

public sealed record RollbackMemoryRequest(int? Version);

public sealed record PruneMemoryHistoryRequest(int? Keep);

/// <summary>为管理员接口创建带隔离范围的记忆存储（测试可替换为临时库）。</summary>
public interface IWebMemoryStoreFactory
{
    IAgentMemoryStore Create(string ownerUserId, bool isAdmin);
}

public sealed class WebMemoryStoreFactory : IWebMemoryStoreFactory
{
    public IAgentMemoryStore Create(string ownerUserId, bool isAdmin) =>
        new AgentMemoryStore(null, new MemoryAccessScope(ownerUserId, isAdmin));
}
