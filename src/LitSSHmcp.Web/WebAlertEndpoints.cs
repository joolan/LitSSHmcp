using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Web;

/// <summary>
/// Alertmanager 兼容的告警入口：按 token 校验后，把 firing/resolved 告警映射为 team incident。
/// 入口不依赖登录 Cookie；token 来自环境变量，避免把告警密钥放进 config.json/前端。
/// </summary>
public static class WebAlertEndpoints
{
    public const string TokenEnvVar = "LITSSH_ALERT_WEBHOOK_TOKEN";
    public const string OwnerEnvVar = "LITSSH_ALERT_OWNER_USER_ID";
    public const string Path = "/api/alerts/alertmanager";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapWebAlertEndpoints(this WebApplication app)
    {
        app.MapPost(Path, async (HttpContext ctx, IConfigService configService,
            IWebMemoryStoreFactory factory, WebAccessLog log) =>
        {
            var expected = Environment.GetEnvironmentVariable(TokenEnvVar);
            if (string.IsNullOrWhiteSpace(expected))
                return Results.Json(new { error = $"未配置 {TokenEnvVar}" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            var provided = ctx.Request.Headers["X-LitSSH-Alert-Token"].ToString();
            if (string.IsNullOrEmpty(provided))
            {
                var auth = ctx.Request.Headers.Authorization.ToString();
                if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    provided = auth["Bearer ".Length..].Trim();
            }
            if (!FixedTimeEquals(expected, provided))
            {
                log.Write("alert_webhook_denied", null, ctx.Connection.RemoteIpAddress?.ToString(), "bad token");
                return Results.Unauthorized();
            }

            AlertmanagerPayload? payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<AlertmanagerPayload>(
                    ctx.Request.Body, JsonOptions, ctx.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "无效的 Alertmanager JSON" });
            }
            if (payload?.Alerts is not { Count: > 0 })
                return Results.BadRequest(new { error = "alerts 不能为空" });

            var config = await configService.LoadConfigAsync();
            var owner = Environment.GetEnvironmentVariable(OwnerEnvVar);
            if (string.IsNullOrWhiteSpace(owner)) owner = "alert-webhook";
            var store = factory.Create(owner!, isAdmin: true);
            var created = new List<long>();
            var skipped = 0;
            foreach (var alert in payload.Alerts)
            {
                if (alert is null) { skipped++; continue; }
                alert.Labels = new Dictionary<string, string>(alert.Labels ?? new(),
                    StringComparer.OrdinalIgnoreCase);
                alert.Annotations = new Dictionary<string, string>(alert.Annotations ?? new(),
                    StringComparer.OrdinalIgnoreCase);
                var status = (alert.Status ?? payload.Status ?? string.Empty).Trim().ToLowerInvariant();
                if (status is not ("firing" or "resolved"))
                {
                    skipped++;
                    continue;
                }
                var assetIds = AlertIncidentMapper.ResolveAssetIds(config, alert.Labels);
                var key = string.IsNullOrWhiteSpace(alert.Fingerprint)
                    ? AlertIncidentMapper.Fingerprint(alert)
                    : alert.Fingerprint!.Trim();
                var sessionId = "alert:" + key;
                var open = await store.ListIncidentsAsync(assetIds.Length > 0 ? assetIds : null,
                    "open", 200, ctx.RequestAborted);
                var matched = open.FirstOrDefault(i => string.Equals(i.SessionId, sessionId,
                    StringComparison.Ordinal));

                if (status == "firing" && matched is not null)
                {
                    skipped++;
                    continue;
                }
                if (status == "resolved" && matched is null)
                {
                    skipped++;
                    continue;
                }

                var title = AlertIncidentMapper.Title(alert, status);
                var symptom = AlertIncidentMapper.Symptom(alert);
                var evidence = AlertIncidentMapper.Evidence(alert);
                var incidentStatus = status == "resolved" ? "resolved" : "open";
                var incidentId = await store.AddIncidentAsync(
                    title, symptom, assetIds, string.Empty,
                    "由 Alertmanager webhook 自动创建", status,
                    evidence, 0.5, incidentStatus, sessionId, ctx.RequestAborted);
                if (status == "resolved" && matched is not null)
                    await store.SupersedeIncidentAsync(matched.Id, incidentId, ctx.RequestAborted);
                created.Add(incidentId);
            }

            log.Write("alert_webhook", null, ctx.Connection.RemoteIpAddress?.ToString(),
                $"created={created.Count} skipped={skipped}");
            return Results.Ok(new { created = created.Count, skipped, incidentIds = created });
        });
    }

    private static bool FixedTimeEquals(string expected, string provided)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(provided);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

public sealed class AlertmanagerPayload
{
    public string? Status { get; set; }
    public List<AlertmanagerAlert> Alerts { get; set; } = new();
}

public sealed class AlertmanagerAlert
{
    public string? Status { get; set; }
    public string? Fingerprint { get; set; }
    public Dictionary<string, string> Labels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Annotations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? StartsAt { get; set; }
    public string? EndsAt { get; set; }
}

public static class AlertIncidentMapper
{
    private static readonly string[] AssetLabelKeys =
        ["serverId", "server_id", "server", "hostname", "host", "instance", "assetId", "asset"];

    public static string ResolveAssetIds(AppConfig config, IReadOnlyDictionary<string, string> labels)
    {
        foreach (var key in AssetLabelKeys)
        {
            if (!labels.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                continue;
            value = value.Trim();
            if (value.StartsWith("ssh:", StringComparison.OrdinalIgnoreCase))
                return "ssh:" + value[4..];
            var host = StripPort(value);
            var server = config.Servers.FirstOrDefault(s =>
                string.Equals(s.Id, value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Name, value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Host, value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase));
            if (server is not null)
                return "ssh:" + server.Id;
        }
        return string.Empty;
    }

    public static string Title(AlertmanagerAlert alert, string status)
    {
        var summary = alert.Annotations.TryGetValue("summary", out var s) && !string.IsNullOrWhiteSpace(s)
            ? s.Trim()
            : alert.Labels.TryGetValue("alertname", out var name) && !string.IsNullOrWhiteSpace(name)
                ? name.Trim()
                : "Alertmanager alert";
        return $"[{status}] {summary}";
    }

    public static string Symptom(AlertmanagerAlert alert)
    {
        var description = alert.Annotations.TryGetValue("description", out var d) && !string.IsNullOrWhiteSpace(d)
            ? d.Trim()
            : alert.Annotations.TryGetValue("summary", out var s) ? s.Trim() : string.Empty;
        var labels = string.Join(", ", alert.Labels.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key}={kv.Value}"));
        var text = $"{description}\nlabels: {labels}".Trim();
        return text.Length <= 4000 ? text : text[..4000];
    }

    public static string Evidence(AlertmanagerAlert alert)
    {
        var json = JsonSerializer.Serialize(alert, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return json.Length <= 4000 ? json : json[..4000];
    }

    public static string Fingerprint(AlertmanagerAlert alert)
    {
        var text = string.Join("|", alert.Labels.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key}={kv.Value}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string StripPort(string value)
    {
        var index = value.LastIndexOf(':');
        return index > 0 && value.IndexOf(':') == index ? value[..index] : value;
    }
}
