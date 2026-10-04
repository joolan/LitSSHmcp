// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + get_usage_guide 内置清单(由注解反射生成, 无需手改);
//   ③ 若新增了工具类, 记得在 Program.cs 注册 WithTools<T>()。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。详见 docs/TOOLS.md 顶部"同步约定"。
using System.ComponentModel;
using System.Text.Json;
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
    [Description("查询服务器整机快照(默认最新一份: 资源/端口↔进程↔服务/Docker容器/nginx证书与域名/systemd健康/安全巡检[SSH·防火墙·fail2ban·MySQL高危账户·高危端口] + 采集事件与最近历史)。排查服务器问题时优先看它; 从未生成过会提示用 ssh_snapshot_refresh 生成")]
    public async Task<SnapshotDto> GetSnapshot(
        [Description("服务器标识: ID/名称/主机名, 可用ssh_list_servers列出")] string serverId,
        [Description("可选: 指定快照ID取某一份历史; 不传则取该服务器最新一份")] long? snapshotId = null,
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

        return BuildDto(server, snapshot, events, recent, success: true, status: "ok");
    }

    [McpServerTool(Name = "ssh_snapshot_refresh", UseStructuredContent = true, OutputSchemaType = typeof(SnapshotDto), ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = true)]
    [Description("重新采集服务器整机快照(资源/端口进程服务/Docker容器/nginx证书/systemd/安全巡检)。耗时较长(典型10-30秒,弱网更久);同一服务器同时只允许一个快照,进行中返回 snapshot_in_progress,稍后用 ssh_snapshot_get 查询")]
    public async Task<SnapshotDto> RefreshSnapshot(
        [Description("服务器标识: ID/名称/主机名, 可用ssh_list_servers列出")] string serverId,
        CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadConfigAsync();
        var (server, resolveStatus, resolveError) = ToolSupport.ResolveServer(config, serverId);
        if (server == null)
            return SnapshotDto.Fail(resolveStatus ?? "server_not_found", resolveError!);

        var options = config.Snapshot ?? new SnapshotConfig();
        var result = await _snapshotService.RefreshAsync(server, options, cancellationToken);

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
            error: succeeded ? null : result.Error);
        response.SnapshotId ??= result.SnapshotId;
        if (!succeeded)
            response.Hint = "快照失败原因见 events 与各 section 的 status/error; 可稍后重试 ssh_snapshot_refresh";
        return response;
    }

    private static SnapshotDto BuildDto(
        SshServerConfig server,
        ServerSnapshot? snapshot,
        List<SnapshotEvent> events,
        List<ServerSnapshot> recent,
        bool success,
        string status,
        string? error = null)
    {
        var dto = new SnapshotDto
        {
            Success = success,
            Status = status,
            Error = error,
            ServerId = server.Id,
            ServerName = server.Name,
            Host = server.Host,
            Events = events.Select(ToEventDto).ToList(),
            Recent = recent.Select(r => new SnapshotSummaryDto
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
        dto.Data = DeserializeData(snapshot.DataJson);
        return dto;
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
