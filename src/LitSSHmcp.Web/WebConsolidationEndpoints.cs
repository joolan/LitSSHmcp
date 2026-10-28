using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Web;

namespace LitSSHmcp.Web;

/// <summary>管理员 consolidation 审核接口：候选列表/批准/拒绝、事件查看/supersede。</summary>
public static class WebConsolidationEndpoints
{
    public static void MapWebConsolidationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/consolidation/runs", async (HttpContext ctx, WebAccessLog log,
            IWebMemoryStoreFactory factory, int? limit) =>
        {
            var principal = RequireAdmin(ctx, log);
            if (principal is null) return Results.Empty;
            var store = factory.Create(principal.UserId.ToString(), isAdmin: true);
            var records = await store.ListConsolidationRunsAsync(limit ?? 50, ctx.RequestAborted);
            return Results.Ok(new
            {
                items = records.Select(r => new
                {
                    id = r.Id,
                    startedAt = r.StartedAt,
                    finishedAt = r.FinishedAt,
                    status = r.Status,
                    rawCount = r.RawCount,
                    candidateCount = r.CandidateCount,
                    appliedCount = r.AppliedCount,
                    error = r.Error
                })
            });
        });

        var candidates = app.MapGroup("/api/admin/consolidation/candidates");

        candidates.MapGet("/", async (HttpContext ctx, WebAccessLog log, IWebMemoryStoreFactory factory,
            string? status, int? limit) =>
        {
            var principal = RequireAdmin(ctx, log);
            if (principal is null) return Results.Empty;

            var store = factory.Create(principal.UserId.ToString(), isAdmin: true);
            var records = await store.ListConsolidationCandidatesAsync(status?.Trim(), limit ?? 50,
                ctx.RequestAborted);
            return Results.Ok(new
            {
                items = records.Select(c => new
                {
                    id = c.Id,
                    kind = c.Kind,
                    payload = c.PayloadJson,
                    assetIds = c.AssetIds,
                    evidence = c.Evidence,
                    confidence = c.Confidence,
                    status = c.Status,
                    sourceRefs = c.SourceRefs,
                    createdAt = c.CreatedAt,
                    updatedAt = c.UpdatedAt,
                    appliedRef = c.AppliedRef,
                    rejectReason = c.RejectReason
                })
            });
        });

        candidates.MapPost("/{id:long}/approve", async (long id, HttpContext ctx, WebAccessLog log,
            IWebMemoryStoreFactory factory, IConfigService configService) =>
        {
            var principal = RequireAdmin(ctx, log);
            if (principal is null) return Results.Empty;

            var store = factory.Create(principal.UserId.ToString(), isAdmin: true);
            var memory = await CreateMemoryAsync(configService, principal, ctx.RequestAborted);
            var review = new ConsolidationReviewService(store, memory);
            var (ok, message, incidentId) = await review.ApproveAsync(id, ctx.RequestAborted);
            log.Write(ok ? "consolidation_approve" : "consolidation_approve_failed", principal.Username,
                ctx.Connection.RemoteIpAddress?.ToString(), $"candidate={id} {message}");
            return ok
                ? Results.Ok(new { status = "applied", incidentId, message })
                : Results.BadRequest(new { error = message });
        });

        candidates.MapPost("/{id:long}/reject", async (long id, RejectConsolidationCandidateRequest? req,
            HttpContext ctx, WebAccessLog log, IWebMemoryStoreFactory factory) =>
        {
            var principal = RequireAdmin(ctx, log);
            if (principal is null) return Results.Empty;

            var store = factory.Create(principal.UserId.ToString(), isAdmin: true);
            var review = new ConsolidationReviewService(store);
            var (ok, message) = await review.RejectAsync(id, req?.Reason, ctx.RequestAborted);
            log.Write(ok ? "consolidation_reject" : "consolidation_reject_failed", principal.Username,
                ctx.Connection.RemoteIpAddress?.ToString(), $"candidate={id}");
            return ok ? Results.Ok(new { status = "rejected", message })
                : Results.BadRequest(new { error = message });
        });

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
            var review = new ConsolidationReviewService(store);
            var (ok, message) = await review.SupersedeIncidentAsync(id, req.NewId.Value, ctx.RequestAborted);
            log.Write(ok ? "incident_supersede" : "incident_supersede_failed", principal.Username,
                ctx.Connection.RemoteIpAddress?.ToString(), $"old={id} new={req.NewId}");
            return ok ? Results.Ok(new { status = "superseded", message })
                : Results.BadRequest(new { error = message });
        });
    }

    private static async Task<AgentMemoryService?> CreateMemoryAsync(IConfigService configService,
        WebPrincipal principal, CancellationToken ct)
    {
        var config = await configService.LoadConfigAsync();
        var providers = AgentProviderGroupConfig.Flatten(config.Agent.Providers);
        var provider = providers.FirstOrDefault(p => p.Id == config.Agent.ActiveProviderId)
                       ?? providers.FirstOrDefault();
        return await AgentMemoryService.CreateFromConfigAsync(config.Agent, provider?.ApiKey,
            new MemoryAccessScope(principal.UserId.ToString(), IsAdmin: true), ct);
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

public sealed record RejectConsolidationCandidateRequest(string? Reason);

public sealed record SupersedeIncidentRequest(long? NewId);
