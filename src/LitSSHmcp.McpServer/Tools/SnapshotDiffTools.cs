using System.ComponentModel;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Snapshot;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

/// <summary>比较两次服务器快照：账号/公钥/密码状态、端口、systemd 失败单元、Docker 容器、nginx 域名与配置 hash。</summary>
[McpServerToolType]
public sealed class SnapshotDiffTools
{
    private readonly IConfigService _configService;
    private readonly ISnapshotStore _snapshotStore;

    public SnapshotDiffTools(IConfigService configService, ISnapshotStore snapshotStore)
    {
        _configService = configService;
        _snapshotStore = snapshotStore;
    }

    [McpServerTool(Name = "ssh_snapshot_diff", UseStructuredContent = true,
        OutputSchemaType = typeof(SnapshotDiffDto), ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("比较两次服务器快照的差异：账号/公钥/密码状态变化、端口增删、systemd 新失败/恢复、Docker 容器增删、nginx 域名与配置 hash 变化。不传快照 ID 时比较最近两条成功快照。")]
    public async Task<SnapshotDiffDto> Diff(
        [Description("服务器 ID/名称/主机名")] string serverId,
        [Description("可选: 起始快照 ID；不传默认上一条成功快照")] long? fromSnapshotId = null,
        [Description("可选: 目标快照 ID；不传默认最新成功快照")] long? toSnapshotId = null,
        CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadConfigAsync();
        var (server, resolveStatus, resolveError) = ToolSupport.ResolveServer(config, serverId);
        if (server is null)
            return SnapshotDiffDto.Fail(resolveStatus ?? "server_not_found", resolveError!);

        ServerSnapshot? from;
        ServerSnapshot? to;
        if (fromSnapshotId is > 0 || toSnapshotId is > 0)
        {
            if (fromSnapshotId is not > 0 || toSnapshotId is not > 0)
                return SnapshotDiffDto.Fail("invalid_argument", "fromSnapshotId 与 toSnapshotId 必须同时提供，或都不传。");

            from = await _snapshotStore.GetByIdAsync(fromSnapshotId.Value, cancellationToken);
            to = await _snapshotStore.GetByIdAsync(toSnapshotId.Value, cancellationToken);
            if (from is null || to is null)
                return SnapshotDiffDto.Fail("snapshot_not_found", "指定的快照不存在。");
            if (!string.Equals(from.ServerId, server.Id, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(to.ServerId, server.Id, StringComparison.OrdinalIgnoreCase))
                return SnapshotDiffDto.Fail("server_mismatch", "指定的快照不属于该服务器。");
        }
        else
        {
            var recent = await _snapshotStore.GetRecentWithDataAsync(server.Id, 10, cancellationToken);
            var successful = recent.Where(s => s.Status == SnapshotStatus.Succeeded).ToList();
            if (successful.Count < 2)
                return SnapshotDiffDto.Fail("snapshot_not_enough", "至少需要两条成功快照才能比较。");
            to = successful[0];
            from = successful[1];
        }

        var changes = SnapshotDiffService.Compare(from.DataJson, to.DataJson);
        return new SnapshotDiffDto
        {
            Success = true,
            Status = "ok",
            ServerId = server.Id,
            ServerName = server.Name,
            FromSnapshotId = from.Id,
            ToSnapshotId = to.Id,
            FromCreatedAt = from.CreatedAt,
            ToCreatedAt = to.CreatedAt,
            Changes = changes
        };
    }
}

public sealed class SnapshotDiffDto
{
    public bool Success { get; set; }
    public string Status { get; set; } = "ok";
    public string? Error { get; set; }
    public string ServerId { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public long? FromSnapshotId { get; set; }
    public long? ToSnapshotId { get; set; }
    public string? FromCreatedAt { get; set; }
    public string? ToCreatedAt { get; set; }
    public Dictionary<string, object?>? Changes { get; set; }

    public static SnapshotDiffDto Fail(string status, string error) => new()
    {
        Success = false,
        Status = status,
        Error = error
    };
}
