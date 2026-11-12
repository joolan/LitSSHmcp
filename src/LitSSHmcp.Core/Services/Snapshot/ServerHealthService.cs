using System.Text.Json;

namespace LitSSHmcp.Core.Services.Snapshot;

public sealed record ServerHealthFinding(string Severity, string Title, string Detail);

public sealed record ServerHealthResult(
    int Score,
    string Level,
    string Summary,
    IReadOnlyList<ServerHealthFinding> Findings)
{
    public bool HasIssue => Score < 90 || Findings.Count > 0;
}

/// <summary>
/// 基于已有快照的轻量健康评分：不引入 Prometheus，直接复用 resource/systemd/security/accounts/nginx_tls。
/// 分数只用于排序与告警，最终结论仍以具体证据为准。
/// </summary>
public static class ServerHealthService
{
    public static ServerHealthResult Evaluate(string? dataJson, string? serverName = null)
    {
        var findings = new List<ServerHealthFinding>();
        var score = 100;
        var name = string.IsNullOrWhiteSpace(serverName) ? "服务器" : serverName.Trim();

        if (string.IsNullOrWhiteSpace(dataJson))
            return new ServerHealthResult(0, "unknown", $"{name}: 无快照数据", findings);

        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            if (!doc.RootElement.TryGetProperty("sections", out var sections) ||
                sections.ValueKind != JsonValueKind.Object)
                return new ServerHealthResult(0, "unknown", $"{name}: 快照无 sections", findings);

            score -= EvaluateResource(sections, findings);
            score -= EvaluateSystemd(sections, findings);
            score -= EvaluateSecurity(sections, findings);
            score -= EvaluateAccounts(sections, findings);
            score -= EvaluateNginx(sections, findings);
        }
        catch (JsonException)
        {
            return new ServerHealthResult(0, "unknown", $"{name}: 快照数据损坏", findings);
        }

        score = Math.Clamp(score, 0, 100);
        var level = score >= 90 ? "healthy" : score >= 70 ? "warning" : "critical";
        var summary = $"{name}: 健康分 {score}/100（{LevelText(level)}）"
                      + (findings.Count == 0 ? "，未发现明显风险" : $"，主要风险 {findings.Count} 项");
        return new ServerHealthResult(score, level, summary, findings
            .OrderByDescending(f => SeverityRank(f.Severity))
            .ThenBy(f => f.Title, StringComparer.Ordinal)
            .ToList());
    }

    private static int EvaluateResource(JsonElement sections, List<ServerHealthFinding> findings)
    {
        if (!sections.TryGetProperty("resource", out var section) ||
            !section.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return 0;

        var deduction = 0;
        if (data.TryGetProperty("memory", out var memory))
        {
            var used = Number(memory, "usedPercent");
            if (used >= 95) { deduction += 20; Add(findings, "high", "内存接近耗尽", $"内存使用率 {used:F1}%"); }
            else if (used >= 85) { deduction += 10; Add(findings, "medium", "内存使用偏高", $"内存使用率 {used:F1}%"); }
            var swap = Number(memory, "swapUsedPercent");
            if (swap >= 80) { deduction += 5; Add(findings, "medium", "Swap 使用偏高", $"Swap 使用率 {swap:F1}%"); }
        }

        var maxDisk = 0d;
        if (data.TryGetProperty("disks", out var disks) && disks.ValueKind == JsonValueKind.Array)
            maxDisk = disks.EnumerateArray().Select(d => Number(d, "usedPercent")).DefaultIfEmpty(0).Max();
        if (maxDisk >= 95) { deduction += 20; Add(findings, "high", "磁盘接近写满", $"最高磁盘使用率 {maxDisk:F1}%"); }
        else if (maxDisk >= 85) { deduction += 10; Add(findings, "medium", "磁盘使用偏高", $"最高磁盘使用率 {maxDisk:F1}%"); }

        var load = Number(data, "load", "1m");
        var cores = Math.Max(1, Number(data, "cpu", "cores"));
        if (load / cores >= 2) { deduction += 10; Add(findings, "high", "系统负载过高", $"1m 负载 {load:F2} / {cores:F0} 核"); }
        else if (load / cores >= 1) { deduction += 5; Add(findings, "medium", "系统负载偏高", $"1m 负载 {load:F2} / {cores:F0} 核"); }

        if (data.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array)
            foreach (var warning in warnings.EnumerateArray().Take(3))
            {
                var text = warning.ToString();
                if (text.Length > 120) text = text[..120] + "…";
                Add(findings, "low", "资源告警", text);
            }

        return Math.Min(deduction, 35);
    }

    private static int EvaluateSystemd(JsonElement sections, List<ServerHealthFinding> findings)
    {
        if (!sections.TryGetProperty("systemd", out var section) ||
            !section.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return 0;

        var failed = 0;
        if (data.TryGetProperty("failed", out var failedArray) && failedArray.ValueKind == JsonValueKind.Array)
            failed = failedArray.GetArrayLength();
        else if (data.TryGetProperty("counts", out var counts))
            failed = (int)Number(counts, "failed");

        if (failed <= 0)
            return 0;
        Add(findings, failed >= 3 ? "high" : "medium", "systemd 存在失败服务", $"failed={failed}");
        return Math.Min(failed * 8, 20);
    }

    private static int EvaluateSecurity(JsonElement sections, List<ServerHealthFinding> findings)
    {
        if (!sections.TryGetProperty("security", out var section) ||
            !section.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return 0;

        var high = 0;
        var medium = 0;
        if (data.TryGetProperty("summary", out var summary))
        {
            high = (int)Number(summary, "high");
            medium = (int)Number(summary, "medium");
        }
        if (high > 0) Add(findings, "high", "安全高风险", $"high={high}, medium={medium}");
        else if (medium > 0) Add(findings, "medium", "安全中风险", $"medium={medium}");

        if (data.TryGetProperty("exposedHighRiskPorts", out var ports) && ports.ValueKind == JsonValueKind.Array)
        {
            var count = ports.GetArrayLength();
            if (count > 0)
            {
                Add(findings, count >= 3 ? "high" : "medium", "暴露高危端口", $"数量={count}");
                high = Math.Max(high, Math.Min(count, 4));
            }
        }

        return Math.Min(high * 8 + medium * 4, 35);
    }

    private static int EvaluateAccounts(JsonElement sections, List<ServerHealthFinding> findings)
    {
        if (!sections.TryGetProperty("accounts", out var section) ||
            !section.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return 0;

        var high = 0;
        var medium = 0;
        var risky = 0;
        if (data.TryGetProperty("summary", out var summary))
        {
            high = (int)Number(summary, "high");
            medium = (int)Number(summary, "medium");
            risky = (int)Number(summary, "riskyUsers");
        }
        if (high > 0) Add(findings, "high", "账号高风险", $"high={high}, riskyUsers={risky}");
        else if (medium > 0) Add(findings, "medium", "账号中风险", $"medium={medium}, riskyUsers={risky}");
        return Math.Min(high * 6 + medium * 3, 20);
    }

    private static int EvaluateNginx(JsonElement sections, List<ServerHealthFinding> findings)
    {
        if (!sections.TryGetProperty("nginx_tls", out var section) ||
            !section.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return 0;

        var expiration = 0;
        if (data.TryGetProperty("expiringSoon", out var soon) && soon.ValueKind == JsonValueKind.Array)
            expiration = soon.GetArrayLength();
        if (expiration > 0)
        {
            Add(findings, "high", "证书即将到期", $"数量={expiration}");
            return Math.Min(expiration * 8, 20);
        }

        if (data.TryGetProperty("configFallback", out var fallback) && fallback.ValueKind == JsonValueKind.True)
        {
            Add(findings, "low", "nginx 配置解析使用兜底", "建议检查 nginx -T 是否可用");
            return 3;
        }
        return 0;
    }

    private static void Add(List<ServerHealthFinding> findings, string severity, string title, string detail)
    {
        if (findings.Any(f => string.Equals(f.Title, title, StringComparison.Ordinal)))
            return;
        findings.Add(new ServerHealthFinding(severity, title, detail));
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0
    };

    private static string LevelText(string level) => level switch
    {
        "healthy" => "健康",
        "warning" => "关注",
        "critical" => "风险",
        _ => "未知"
    };

    private static double Number(JsonElement element, string property, string? nested = null)
    {
        if (!element.TryGetProperty(property, out var value))
            return 0;
        if (nested is not null)
            return value.ValueKind == JsonValueKind.Object ? Number(value, nested) : 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : 0;
    }
}
