using System.ComponentModel;
using System.Text.Json;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Resources;

/// <summary>
/// MCP 资源（模板）：某服务器**最新一份整机快照的概览**（只读，等价 <c>ssh_snapshot_get</c> 的概览）。
/// URI 模板 <c>litssh://server/{id}/snapshot/latest</c>，{id} = 服务器 Id / 名称 / 主机名。
/// </summary>
[McpServerResourceType]
public class SnapshotResources
{
    private readonly ISnapshotStore _store;
    private readonly IConfigService _configService;

    public SnapshotResources(ISnapshotStore store, IConfigService configService)
    {
        _store = store;
        _configService = configService;
    }

    [McpServerResource(UriTemplate = "litssh://server/{id}/snapshot/latest",
        Name = "litssh-server-snapshot-latest", Title = "服务器最新快照概览", MimeType = "application/json")]
    [Description("某服务器最新一份整机快照的概览（resource 维度：CPU/内存/磁盘/负载 + warnings/riskLevel，及各采集维度状态）。{id} = 服务器 Id/名称/主机名。")]
    public async Task<string> GetLatestSnapshot(string id)
    {
        var config = await _configService.LoadConfigAsync();
        var (server, status, error) = ToolSupport.ResolveServer(config, id ?? string.Empty);
        if (server is null)
            return JsonSerializer.Serialize(new { found = false, status, error }, SshResources.Json);

        var snapshot = await _store.GetLatestAsync(server.Id);
        if (snapshot is null)
            return JsonSerializer.Serialize(new
            {
                found = false,
                status = "snapshot_not_found",
                error = $"服务器 {server.Name} 还没有快照，可用 ssh_snapshot_refresh 生成。",
                serverId = server.Id,
                serverName = server.Name
            }, SshResources.Json);

        var result = new Dictionary<string, object?>
        {
            ["found"] = true,
            ["serverId"] = server.Id,
            ["serverName"] = server.Name,
            ["state"] = snapshot.Status.ToString().ToLowerInvariant(),
            ["createdAt"] = snapshot.CreatedAt,
            ["completedAt"] = snapshot.CompletedAt,
            ["escalation"] = snapshot.Escalation,
            ["collectorVersion"] = snapshot.CollectorVersion,
            ["durationMs"] = snapshot.DurationMs,
            ["error"] = snapshot.Error
        };

        if (!string.IsNullOrWhiteSpace(snapshot.DataJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(snapshot.DataJson!);
                if (doc.RootElement.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Object)
                {
                    var sectionStatuses = new Dictionary<string, object?>();
                    foreach (var section in sections.EnumerateObject())
                    {
                        var st = section.Value.TryGetProperty("status", out var s) ? s.GetString() : null;
                        var err = section.Value.TryGetProperty("error", out var e) ? e.GetString() : null;
                        sectionStatuses[section.Name] = new { status = st, error = err };
                    }
                    result["sections"] = sectionStatuses;

                    // resource 维度已含 warnings / riskLevel，直接作为概览。
                    if (sections.TryGetProperty("resource", out var res) && res.TryGetProperty("data", out var data))
                        result["resource"] = JsonSerializer.Deserialize<object>(data.GetRawText());
                }
            }
            catch (JsonException)
            {
                // 数据损坏则只回基础字段
            }
        }

        return JsonSerializer.Serialize(result, SshResources.Json);
    }
}
