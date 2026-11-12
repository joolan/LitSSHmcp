// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + mcp_usage_guide 内置清单(由注解反射生成, 无需手改);
//   ③ 若新增了工具类, 记得在 Program.cs 注册 WithTools<T>()。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。详见 docs/TOOLS.md 顶部"同步约定"。
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Snapshot;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

/// <summary>
/// 服务器快照工具：把整机态势（资源/端口进程服务三元组/nginx证书/systemd健康）采集并持久化到本机
/// snapshots.db，供随时按 serverId 查询；重新采集为较慢操作，同一服务器单飞限流，失败也会落库状态。
/// </summary>
[McpServerToolType]
public class SnapshotTools
{
    private readonly IConfigService _configService;
    private readonly ISnapshotStore _snapshotStore;
    private readonly ISnapshotService _snapshotService;
    private readonly IAuditLogService _auditLogService;

    public SnapshotTools(
        IConfigService configService,
        ISnapshotStore snapshotStore,
        ISnapshotService snapshotService,
        IAuditLogService auditLogService)
    {
        _configService = configService;
        _snapshotStore = snapshotStore;
        _snapshotService = snapshotService;
        _auditLogService = auditLogService;
    }

    [McpServerTool(Name = "ssh_snapshot_get", UseStructuredContent = true, OutputSchemaType = typeof(SnapshotDto), ReadOnly = true, OpenWorld = true)]
    [Description("查询服务器整机快照(默认最新一份)。默认只返回各维度概览(status/关键指标/数据大小)，需要完整数据用 section 指定或 detail=\"full\"。从未生成过会提示用 ssh_snapshot_refresh")]
    public async Task<SnapshotDto> GetSnapshot(
        [Description("服务器 ID/名称/主机名")] string serverId,
        [Description("可选: 指定快照ID取某一份历史; 不传则取该服务器最新一份")] long? snapshotId = null,
        [Description("可选: 只取某维度完整数据, 取值 resource/portmap/docker/nginx_tls/systemd/security/accounts; 不传则返回概览")] string? section = null,
        [Description("可选: summary(默认,各维度概览)/full(全部完整数据,较大)")] string? detail = null,
        [Description("可选: accounts 维度按用户名过滤（精确或包含，忽略大小写）")] string? account = null,
        [Description("可选: accounts 维度按风险过滤 high/medium/normal（也接受 高/中/正常）")] string? riskLevel = null,
        CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadConfigAsync();
        var (server, resolveStatus, resolveError) = ToolSupport.ResolveServer(config, serverId);
        if (server == null)
            return SnapshotDto.Fail(resolveStatus ?? "server_not_found", resolveError!);

        var snapshot = snapshotId is > 0
            ? await _snapshotStore.GetByIdAsync(snapshotId.Value, cancellationToken)
            : await _snapshotStore.GetLatestAsync(server.Id, cancellationToken);

        if (snapshot == null)
        {
            var dto = SnapshotDto.Fail("snapshot_not_found",
                snapshotId is > 0
                    ? $"未找到快照 #{snapshotId}"
                    : $"服务器 {server.Name} 还没有快照");
            dto.Hint = $"调用 ssh_snapshot_refresh(serverId=\"{server.Id}\") 生成首份快照";
            dto.ServerId = server.Id;
            dto.ServerName = server.Name;
            dto.Host = server.Host;
            return dto;
        }

        var events = await _snapshotStore.GetEventsAsync(snapshot.Id, 50, cancellationToken);
        var recent = await _snapshotStore.GetRecentAsync(server.Id, 10, cancellationToken);
        await LogAuditAsync(server.Id, server.Name, $"GET_SNAPSHOT #{snapshot.Id}",
            snapshot.Status.ToString(), CommandStatus.Executed, AuditCategory.Meta);

        var resultDto = BuildDto(server, snapshot, events, recent, success: true, status: "ok",
            section: section, full: IsFull(detail), account: account, riskLevel: riskLevel);
        if (IsFull(detail) && (string.IsNullOrWhiteSpace(section) ||
                               string.Equals(section.Trim(), "nginx_tls", StringComparison.OrdinalIgnoreCase)))
            await TryInjectNginxConfigAsync(server, snapshot, resultDto.Data, cancellationToken);
        return resultDto;
    }

    [McpServerTool(Name = "ssh_snapshot_refresh", UseStructuredContent = true, OutputSchemaType = typeof(SnapshotDto), ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = true)]
    [Description("重新采集服务器整机快照(典型10-30秒，同一服务器单飞)。距上次成功快照不足60s且未传 force=true 时返回已有快照(status=fresh)；默认仅返回概览，可用 section/detail 取完整数据")]
    public async Task<SnapshotDto> RefreshSnapshot(
        [Description("服务器 ID/名称/主机名")] string serverId,
        [Description("可选: 返回某维度完整数据, 取值 resource/portmap/docker/nginx_tls/systemd/security/accounts; 不传则返回概览")] string? section = null,
        [Description("可选: summary(默认,各维度概览)/full(全部完整数据,较大)")] string? detail = null,
        [Description("可选: 忽略最短刷新间隔强制重采(默认 false)")] bool force = false,
        [Description("可选: quick=resource+portmap+systemd+security；full=全部（默认）")] string? profile = null,
        [Description("可选: 只采集指定维度，逗号分隔，覆盖 profile；如 resource,portmap,security")] string? sections = null,
        CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadConfigAsync();
        var (server, resolveStatus, resolveError) = ToolSupport.ResolveServer(config, serverId);
        if (server == null)
            return SnapshotDto.Fail(resolveStatus ?? "server_not_found", resolveError!);

        var options = config.Snapshot ?? new SnapshotConfig();

        var collectSections = ParseCollectSections(profile, sections);
        var result = await _snapshotService.RefreshAsync(server, options, collectSections, force, cancellationToken);

        if (result.Status == "fresh")
        {
            var cachedSnapshot = result.Snapshot
                ?? await _snapshotStore.GetLatestAsync(server.Id, cancellationToken);
            if (cachedSnapshot is not null)
            {
                var cachedEvents = await _snapshotStore.GetEventsAsync(cachedSnapshot.Id, 50, cancellationToken);
                var cachedRecent = await _snapshotStore.GetRecentAsync(server.Id, 10, cancellationToken);
                var cached = BuildDto(server, cachedSnapshot, cachedEvents, cachedRecent, success: true,
                    status: "fresh", section: section, full: IsFull(detail));
                if (IsFull(detail) && (string.IsNullOrWhiteSpace(section) ||
                                       string.Equals(section.Trim(), "nginx_tls", StringComparison.OrdinalIgnoreCase)))
                    await TryInjectNginxConfigAsync(server, cachedSnapshot, cached.Data, cancellationToken);
                cached.Hint = result.Hint;
                return cached;
            }
        }

        if (result.Status == "snapshot_in_progress")
        {
            var dto = SnapshotDto.Fail("snapshot_in_progress", result.Error ?? "该服务器已有快照在采集中");
            dto.Hint = "快照仍在采集中, 请稍后用 ssh_snapshot_get 查询(不要重复刷新)";
            dto.ServerId = server.Id;
            dto.ServerName = server.Name;
            dto.Host = server.Host;
            dto.SnapshotId = result.SnapshotId;
            dto.State = result.Snapshot?.Status.ToString().ToLowerInvariant() ?? "running";
            if (result.Snapshot != null)
            {
                dto.CreatedAt = result.Snapshot.CreatedAt;
                var runningEvents = await _snapshotStore.GetEventsAsync(result.Snapshot.Id, 50, cancellationToken);
                dto.Events = runningEvents.Select(ToEventDto).ToList();
            }
            return dto;
        }

        var succeeded = result.Status == "succeeded";
        await LogAuditAsync(server.Id, server.Name, "SNAPSHOT_REFRESH",
            succeeded
                ? $"#{result.SnapshotId} {result.Snapshot?.DurationMs:F0}ms"
                : result.Error,
            succeeded ? CommandStatus.Executed : CommandStatus.Failed,
            AuditCategory.Probe);

        var snapshot = result.Snapshot;
        var events = snapshot != null
            ? await _snapshotStore.GetEventsAsync(snapshot.Id, 50, cancellationToken)
            : new List<SnapshotEvent>();
        var recent = await _snapshotStore.GetRecentAsync(server.Id, 10, cancellationToken);

        var response = BuildDto(server, snapshot, events, recent,
            success: succeeded,
            status: succeeded ? "succeeded" : "failed",
            error: succeeded ? null : result.Error,
            section: section, full: IsFull(detail));
        if (snapshot is not null && IsFull(detail) &&
            (string.IsNullOrWhiteSpace(section) ||
             string.Equals(section.Trim(), "nginx_tls", StringComparison.OrdinalIgnoreCase)))
            await TryInjectNginxConfigAsync(server, snapshot, response.Data, cancellationToken);
        response.SnapshotId ??= result.SnapshotId;
        if (!succeeded)
            response.Hint = "快照失败原因见 events 与各 section 的 status/error; 可稍后重试 ssh_snapshot_refresh";
        return response;
    }

    /// <summary>profile=quick 只采集轻量只读维度；sections 显式列表优先于 profile。</summary>
    private static IReadOnlyCollection<string>? ParseCollectSections(string? profile, string? sections)
    {
        if (!string.IsNullOrWhiteSpace(sections))
        {
            var list = sections.Split(new[] { ',', '，', ' ', ';' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return list.Length > 0 ? list : null;
        }

        return (profile ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "quick" => new[] { "resource", "portmap", "systemd", "security" },
            _ => null
        };
    }

    private static bool IsFull(string? detail) =>
        string.Equals(detail?.Trim(), "full", StringComparison.OrdinalIgnoreCase);

    internal static bool TryAgeSeconds(string? completedAt, out double ageSeconds)
    {
        ageSeconds = 0;
        if (string.IsNullOrWhiteSpace(completedAt))
            return false;
        if (!DateTime.TryParse(completedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
            return false;
        ageSeconds = (DateTime.UtcNow - when.ToUniversalTime()).TotalSeconds;
        return ageSeconds >= 0;
    }

    /// <summary>detail=full 时从 NginxConfigs 表按 configHash 回填完整 effectiveConfig。</summary>
    private async Task TryInjectNginxConfigAsync(SshServerConfig server, ServerSnapshot snapshot,
        Dictionary<string, object?>? data, CancellationToken ct)
    {
        if (data is null || string.IsNullOrWhiteSpace(snapshot.DataJson))
            return;
        try
        {
            using var doc = JsonDocument.Parse(snapshot.DataJson);
            if (!doc.RootElement.TryGetProperty("sections", out var sections) ||
                !sections.TryGetProperty("nginx_tls", out var nginx) ||
                !nginx.TryGetProperty("data", out var nginxData) ||
                !nginxData.TryGetProperty("configHash", out var hashNode) ||
                hashNode.GetString() is not { Length: > 0 } hash)
                return;

            var content = await _snapshotStore.GetNginxConfigAsync(server.Id, hash, ct);
            if (string.IsNullOrWhiteSpace(content))
                return;

            if (data.TryGetValue("sections", out var sectionsObj) &&
                sectionsObj is Dictionary<string, object?> sectionMap &&
                sectionMap.TryGetValue("nginx_tls", out var nginxObj) &&
                nginxObj is Dictionary<string, object?> nginxMap &&
                nginxMap.TryGetValue("data", out var dataObj) &&
                dataObj is Dictionary<string, object?> target)
                target["effectiveConfig"] = content;
        }
        catch
        {
            // 回填失败不影响快照查询
        }
    }

    private static bool IsSummaryEvent(SnapshotEvent evt) =>
        evt.Kind is "started" or "completed" or "failed" or "collector_failed";

    internal static SnapshotDto BuildDto(
        SshServerConfig server,
        ServerSnapshot? snapshot,
        List<SnapshotEvent> events,
        List<ServerSnapshot> recent,
        bool success,
        string status,
        string? error = null,
        string? section = null,
        bool full = false,
        string? account = null,
        string? riskLevel = null)
    {
        var summaryView = !full && string.IsNullOrWhiteSpace(section);
        var visibleEvents = summaryView
            ? events.Where(IsSummaryEvent).TakeLast(8).ToList()
            : events;
        var visibleRecent = summaryView ? recent.Take(5).ToList() : recent;
        var dto = new SnapshotDto
        {
            Success = success,
            Status = status,
            Error = error,
            ServerId = server.Id,
            ServerName = server.Name,
            Host = server.Host,
            Events = visibleEvents.Select(ToEventDto).ToList(),
            Recent = visibleRecent.Select(r => new SnapshotSummaryDto
            {
                Id = r.Id,
                State = r.Status.ToString().ToLowerInvariant(),
                CreatedAt = r.CreatedAt,
                CompletedAt = r.CompletedAt,
                DurationMs = r.DurationMs,
                Error = r.Error
            }).ToList()
        };

        if (snapshot == null)
            return dto;

        dto.SnapshotId = snapshot.Id;
        dto.State = snapshot.Status.ToString().ToLowerInvariant();
        dto.CreatedAt = snapshot.CreatedAt;
        dto.CompletedAt = snapshot.CompletedAt;
        dto.DurationMs = snapshot.DurationMs;
        dto.Escalation = snapshot.Escalation;
        dto.CollectorVersion = snapshot.CollectorVersion;
        dto.Data = BuildDataView(snapshot.DataJson, section, full, account, riskLevel);
        if (summaryView)
            dto.Hint = "summary 已压缩 events/recent；需要完整事件或全部数据用 detail=\"full\"，单维度用 section=...";
        return dto;
    }

    // —— 快照数据视图：默认概览(各维度关键指标+数据大小)，按 section 取单维度完整数据，或 full 取全部(压缩上限) ——
    private const int SummaryMaxString = 200;
    private const int SummaryMaxArray = 5;
    private const int SectionMaxString = 800;
    private const int SectionMaxArray = 200;
    private const int FullMaxString = 2000;
    private const int FullMaxArray = 1000;

    private static readonly Dictionary<string, string[]> HeadlineKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["resource"] = new[] { "load", "memory", "disks", "cpu", "cores", "os", "hostname", "ips", "topByCpu", "topByMemory", "warnings", "riskLevel" },
        ["portmap"] = new[] { "counts", "truncated", "elevated" },
        ["docker"] = new[] { "available", "daemonRunning", "serverVersion", "counts" },
        ["nginx_tls"] = new[] { "available", "serverType", "version", "configHash", "configFallback", "serverCount", "certificateCount", "expiringSoonDays", "expiringSoon", "domains" },
        ["systemd"] = new[] { "systemState", "total", "counts", "failed" },
        ["security"] = new[] { "summary", "findings", "exposedHighRiskPorts", "emptyPasswordAccounts", "nopasswdSudo" },
        ["accounts"] = new[] { "summary", "findings", "shadowReadable" }
    };

    internal static Dictionary<string, object?>? BuildDataView(string? dataJson, string? section, bool full,
        string? account = null, string? riskLevel = null)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
            return null;

        JsonNode? parsed;
        try { parsed = JsonNode.Parse(dataJson); }
        catch (JsonException) { return DeserializeData(dataJson); }

        if (parsed is not JsonObject root)
            return DeserializeData(dataJson);

        var sections = root["sections"] as JsonObject;

        if (!string.IsNullOrWhiteSpace(section))
        {
            var key = FindSectionKey(sections, section!.Trim());
            var result = BaseMeta(root);
            result["view"] = "section";
            if (key is null)
            {
                result["error"] = $"未知维度 '{section}'";
                result["available"] = BuildAvailable(sections);
            }
            else
            {
                result["sections"] = new JsonObject
                {
                    [key] = CompactSection(key, sections![key], full, account, riskLevel)
                };
            }
            return ToDictionary(result);
        }

        if (full)
        {
            var result = BaseMeta(root);
            result["view"] = "full";
            result["sections"] = Compact(root["sections"], FullMaxString, FullMaxArray);
            return ToDictionary(result);
        }

        var summary = BaseMeta(root);
        summary["view"] = "summary";
        summary["hint"] = "默认仅返回各维度概览; 需要某维度完整数据用 section=resource|portmap|docker|nginx_tls|systemd|security|accounts, 或 detail=\"full\" 取全部";
        var summarySections = new JsonObject();
        if (sections != null)
        {
            foreach (var kv in sections)
            {
                var sec = kv.Value as JsonObject;
                var dataNode = sec?["data"];
                summarySections[kv.Key] = new JsonObject
                {
                    ["status"] = sec?["status"]?.DeepClone(),
                    ["durationMs"] = sec?["durationMs"]?.DeepClone(),
                    ["error"] = sec?["error"]?.DeepClone(),
                    ["note"] = sec?["note"]?.DeepClone(),
                    ["dataChars"] = dataNode?.ToJsonString().Length ?? 0,
                    ["headline"] = BuildHeadline(kv.Key, dataNode)
                };
            }
        }
        summary["sections"] = summarySections;
        return ToDictionary(summary);
    }

    private static JsonObject BaseMeta(JsonObject root)
    {
        var o = new JsonObject();
        if (root.ContainsKey("collectorVersion"))
            o["collectorVersion"] = root["collectorVersion"]?.DeepClone();
        if (root.ContainsKey("elevated"))
            o["elevated"] = root["elevated"]?.DeepClone();
        return o;
    }

    private static JsonArray BuildAvailable(JsonObject? sections)
    {
        var arr = new JsonArray();
        if (sections != null)
            foreach (var kv in sections)
                arr.Add(kv.Key);
        return arr;
    }

    private static string? FindSectionKey(JsonObject? sections, string name)
    {
        if (sections is null)
            return null;
        foreach (var kv in sections)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        return null;
    }

    private static JsonNode? CompactSection(string key, JsonNode? node, bool full,
        string? account = null, string? riskLevel = null)
    {
        if (full)
            return Compact(node, FullMaxString, FullMaxArray);
        if (string.Equals(key, "accounts", StringComparison.OrdinalIgnoreCase))
            return CompactAccountsSection(node, account, riskLevel);
        if (string.Equals(key, "nginx_tls", StringComparison.OrdinalIgnoreCase))
            return CompactNginxSection(node);
        if (string.Equals(key, "portmap", StringComparison.OrdinalIgnoreCase))
            return CompactLimitedSection(node, 50, "端口/Unix socket 默认最多 50 条；完整数据用 detail=\"full\"");
        if (string.Equals(key, "systemd", StringComparison.OrdinalIgnoreCase))
            return CompactLimitedSection(node, 100, "service 单元默认最多 100 条；完整数据用 detail=\"full\"");
        if (string.Equals(key, "docker", StringComparison.OrdinalIgnoreCase))
            return CompactLimitedSection(node, 50, "容器默认最多 50 条；完整数据用 detail=\"full\"");
        return Compact(node, SectionMaxString, SectionMaxArray);
    }

    private static JsonObject CompactLimitedSection(JsonNode? sectionNode, int arrayCap, string hint)
    {
        var section = sectionNode as JsonObject;
        var output = new JsonObject();
        if (section is not null)
        {
            foreach (var key in new[] { "status", "durationMs", "error", "note" })
                if (section.TryGetPropertyValue(key, out var value))
                    output[key] = value?.DeepClone();
        }

        if (section?["data"] is JsonObject data)
        {
            output["data"] = Compact(data, SectionMaxString, arrayCap);
            if (output["data"] is JsonObject compactData)
                compactData["hint"] = hint;
        }
        return output;
    }

    /// <summary>nginx_tls 默认省略完整 effectiveConfig，只保留 hash/预览/站点/证书；全文用 detail=full。</summary>
    private static JsonObject CompactNginxSection(JsonNode? sectionNode)
    {
        var section = sectionNode as JsonObject;
        var output = new JsonObject();
        if (section is not null)
        {
            foreach (var key in new[] { "status", "durationMs", "error", "note" })
                if (section.TryGetPropertyValue(key, out var value))
                    output[key] = value?.DeepClone();
        }

        if (section?["data"] is JsonObject data)
        {
            var copy = (JsonObject)data.DeepClone();
            copy.Remove("effectiveConfig");
            output["data"] = Compact(copy, SectionMaxString, SectionMaxArray);
            if (output["data"] is JsonObject compactData)
                compactData["hint"] = "effectiveConfig 已省略（默认只返回 configHash/configPreview）；完整配置用 detail=\"full\"";
        }
        return output;
    }

    /// <summary>
    /// accounts 默认只返回摘要 + 风险/特权/可登录/有公钥用户，避免 50k+ 的单行 JSON；
    /// 完整 users/authorizedKeys 用 detail="full"。
    /// </summary>
    private static JsonObject CompactAccountsSection(JsonNode? sectionNode,
        string? account = null, string? riskLevel = null)
    {
        var filterAccount = string.IsNullOrWhiteSpace(account) ? null : account.Trim();
        var filterRisk = NormalizeRisk(riskLevel);
        var filtered = filterAccount is not null || filterRisk is not null;
        var section = sectionNode as JsonObject;
        var output = new JsonObject();
        if (section is not null)
        {
            foreach (var key in new[] { "status", "durationMs", "error", "note" })
                if (section.TryGetPropertyValue(key, out var value))
                    output[key] = value?.DeepClone();
        }

        var data = section?["data"] as JsonObject;
        var compact = new JsonObject();
        if (data is not null)
        {
            foreach (var key in new[] { "summary", "shadowReadable" })
                if (data.TryGetPropertyValue(key, out var value))
                    compact[key] = value?.DeepClone();

            if (data["findings"] is JsonArray findings)
                compact["findings"] = Compact(findings, SectionMaxString, 20);

            if (data["users"] is JsonArray users)
            {
                if (filtered)
                {
                    var matched = new JsonArray();
                    foreach (var node in users)
                    {
                        if (node is not JsonObject user || !MatchesAccount(user, filterAccount, filterRisk))
                            continue;
                        matched.Add(ProjectAccountUser(user).DeepClone());
                    }
                    compact["users"] = Take(matched, 50);
                }
                else
                {
                    var risky = new JsonArray();
                    var privileged = new JsonArray();
                    var login = new JsonArray();
                    foreach (var node in users)
                    {
                        if (node is not JsonObject user)
                            continue;
                        var projected = ProjectAccountUser(user);
                        var risk = (string?)user["riskLevel"];
                        if (!string.Equals(risk, "正常", StringComparison.Ordinal) &&
                            !string.IsNullOrWhiteSpace(risk))
                            risky.Add(projected.DeepClone());
                        if ((bool?)user["isPrivileged"] == true)
                            privileged.Add(projected.DeepClone());
                        if (((string?)user["canLogin"])?.StartsWith("可能", StringComparison.Ordinal) == true)
                            login.Add(projected.DeepClone());
                    }
                    compact["riskyUsers"] = Take(risky, 20);
                    compact["privilegedUsers"] = Take(privileged, 20);
                    compact["loginUsers"] = Take(login, 20);
                }
            }

            if (data["authorizedKeys"] is JsonArray keys)
            {
                if (filtered)
                {
                    var matchedKeys = new JsonArray();
                    foreach (var node in keys)
                    {
                        if (node is not JsonObject key || (int?)key["keyCount"] is not > 0)
                            continue;
                        if (filterAccount is not null &&
                            !AccountMatches((string?)key["user"], filterAccount))
                            continue;
                        matchedKeys.Add(key.DeepClone());
                    }
                    compact["authorizedKeys"] = Take(matchedKeys, 20);
                }
                else
                {
                    var keyUsers = new JsonArray();
                    foreach (var node in keys)
                    {
                        if (node is not JsonObject key || (int?)key["keyCount"] is not > 0)
                            continue;
                        keyUsers.Add(new JsonObject
                        {
                            ["user"] = key["user"]?.DeepClone(),
                            ["path"] = key["path"]?.DeepClone(),
                            ["keyCount"] = key["keyCount"]?.DeepClone(),
                            ["modifiedAt"] = key["modifiedAt"]?.DeepClone(),
                            ["mode"] = key["mode"]?.DeepClone()
                        });
                    }
                    compact["keyUsers"] = Take(keyUsers, 20);
                }
            }

            compact["hint"] = filtered
                ? "accounts 已按 account/riskLevel 过滤；完整明细用 detail=\"full\""
                : "accounts 默认返回摘要/风险用户；完整 users/authorizedKeys 用 detail=\"full\"";
        }

        output["data"] = compact;
        return output;
    }

    private static string? NormalizeRisk(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "high" or "高" or "高风险" => "高风险",
        "medium" or "中" or "中风险" => "中风险",
        "normal" or "low" or "正常" => "正常",
        _ => null
    };

    private static bool MatchesAccount(JsonObject user, string? account, string? risk)
    {
        if (account is not null && !AccountMatches((string?)user["name"], account))
            return false;
        if (risk is not null && !string.Equals((string?)user["riskLevel"], risk, StringComparison.Ordinal))
            return false;
        return true;
    }

    private static bool AccountMatches(string? name, string filter) =>
        !string.IsNullOrWhiteSpace(name) &&
        (string.Equals(name, filter, StringComparison.OrdinalIgnoreCase) ||
         name.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private static JsonObject ProjectAccountUser(JsonObject user) => new()
    {
        ["name"] = user["name"]?.DeepClone(),
        ["uid"] = user["uid"]?.DeepClone(),
        ["shell"] = user["shell"]?.DeepClone(),
        ["riskLevel"] = user["riskLevel"]?.DeepClone(),
        ["riskNotes"] = user["riskNotes"]?.DeepClone(),
        ["isPrivileged"] = user["isPrivileged"]?.DeepClone(),
        ["canLogin"] = user["canLogin"]?.DeepClone(),
        ["lastLogin"] = user["lastLogin"]?.DeepClone(),
        ["authorizedKeyCount"] = user["authorizedKeyCount"]?.DeepClone()
    };

    private static JsonArray Take(JsonArray source, int max)
    {
        var result = new JsonArray();
        var count = Math.Min(source.Count, max);
        for (var i = 0; i < count; i++)
            result.Add(source[i]?.DeepClone());
        if (source.Count > count)
            result.Add($"（仅显示前 {count} 条，共 {source.Count} 条；如需全部用 detail=\"full\"）");
        return result;
    }

    private static JsonNode? BuildHeadline(string sectionName, JsonNode? dataNode)
    {
        if (dataNode is not JsonObject data)
            return Compact(dataNode, SummaryMaxString, SummaryMaxArray);

        var headline = new JsonObject();
        if (HeadlineKeys.TryGetValue(sectionName, out var keys))
        {
            foreach (var key in keys)
                if (data.TryGetPropertyValue(key, out var value))
                    headline[key] = Compact(value, SummaryMaxString, SummaryMaxArray);
        }
        else
        {
            foreach (var kv in data)
                headline[kv.Key] = Compact(kv.Value, SummaryMaxString, SummaryMaxArray);
        }
        return headline;
    }

    /// <summary>递归压缩 JSON：截断超长字符串、限制数组长度，给数据视图一个硬上限。</summary>
    private static JsonNode? Compact(JsonNode? node, int maxString, int maxArray)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var o = new JsonObject();
                foreach (var kv in obj)
                    o[kv.Key] = Compact(kv.Value, maxString, maxArray);
                return o;
            case JsonArray arr:
                var a = new JsonArray();
                var take = Math.Min(arr.Count, maxArray);
                for (var i = 0; i < take; i++)
                    a.Add(Compact(arr[i], maxString, maxArray));
                if (arr.Count > take)
                    a.Add($"…(共 {arr.Count} 项, 已省略 {arr.Count - take} 项; 可缩小查询范围或用 section 参数)");
                return a;
            case JsonValue val:
                if (val.TryGetValue<string>(out var s) && s.Length > maxString)
                    return JsonValue.Create(s[..maxString] + "…");
                return val.DeepClone();
            default:
                return node.DeepClone();
        }
    }

    private static Dictionary<string, object?> ToDictionary(JsonObject o)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var kv in o)
            dict[kv.Key] = kv.Value;
        return dict;
    }

    private static SnapshotEventDto ToEventDto(SnapshotEvent e) => new()
    {
        Timestamp = e.Timestamp,
        Kind = e.Kind,
        Collector = e.Collector,
        Message = e.Message
    };

    private static Dictionary<string, object?>? DeserializeData(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
            return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(dataJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task LogAuditAsync(string serverId, string serverName, string command, string? result, CommandStatus status, AuditCategory category) =>
        ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
        {
            ServerId = serverId,
            ServerName = serverName,
            Command = command,
            Result = result,
            Status = status,
            Category = category
        });
}
