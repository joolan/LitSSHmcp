// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + get_usage_guide 内置清单(由注解反射生成, 无需手改);
//   ③ 若新增了工具类, 记得在 Program.cs 注册 WithTools<T>()。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。详见 docs/TOOLS.md 顶部"同步约定"。
using System.ComponentModel;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

[McpServerToolType]
public class DatasourceTools
{
    private readonly IConfigService _configService;
    private readonly IDatasourceDriverRegistry _driverRegistry;
    private readonly IAuditLogService _auditLogService;

    public DatasourceTools(
        IConfigService configService,
        IDatasourceDriverRegistry driverRegistry,
        IAuditLogService auditLogService)
    {
        _configService = configService;
        _driverRegistry = driverRegistry;
        _auditLogService = auditLogService;
    }

    [McpServerTool(Name = "datasource_list", UseStructuredContent = true, OutputSchemaType = typeof(DatasourceListDto), ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("列出MySQL/PostgreSQL/Redis数据源(主机/端口/账号/绑定关系, 无密码)。SSH服务器列表用ssh_list_servers")]
    public async Task<DatasourceListDto> ListDatasources()
    {
        var config = await _configService.LoadConfigAsync();

        var items = config.DataSources.Select(ds => new DatasourceSummaryDto
        {
            Id = ds.Id,
            Name = ds.Name,
            Type = ds.Type,
            Host = ds.Host,
            Port = ds.Port,
            Username = ds.Username,
            DefaultDatabase = ds.DefaultDatabase,
            AccessMode = ds.AccessMode.ToString(),
            TunnelServerId = ds.TunnelServerId,
            TunnelServer = ResolveTunnelServerName(config, ds),
            Description = ds.Description,
            Tags = ds.Tags,
            AccessibleFromSshServers = config.Relations
                .Where(r => r.To == AssetNode.Ds(ds.Id) && r.From.StartsWith(AssetNode.SshPrefix, StringComparison.Ordinal))
                .Select(r => LabelOf(config, r.From))
                .ToArray(),
            ConnectedApplications = config.Relations
                .Where(r => r.To == AssetNode.Ds(ds.Id) && r.From.StartsWith(AssetNode.AppPrefix, StringComparison.Ordinal))
                .Select(r => LabelOf(config, r.From))
                .ToArray()
        }).ToList();

        await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
        {
            ServerId = string.Empty,
            ServerName = string.Empty,
            Command = "LIST_DATASOURCES",
            Result = $"{items.Count} datasources",
            Status = CommandStatus.Executed,
            Category = AuditCategory.Meta
        });

        return new DatasourceListDto { Success = true, Count = items.Count, DataSources = items };
    }

    [McpServerTool(Name = "datasource_test_connection", UseStructuredContent = true, OutputSchemaType = typeof(TestConnectionResultDto), ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("测试数据源(MySQL/PostgreSQL/Redis)连通性, 自动直连或建SSH隧道, 返回AccessMode与ViaTunnelServer。测SSH通信用ssh_test_connection")]
    public async Task<TestConnectionResultDto> GetDatasourceStatus(
        [Description("数据源标识: ID或名称均可, 可用datasource_list列出")] string datasourceId,
        CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadConfigAsync();
        var (ds, resolveStatus, resolveError) = ToolSupport.ResolveDatasource(config, datasourceId);
        if (ds == null)
            return TestConnectionResultDto.Fail(resolveStatus!, resolveError!);
        var driver = _driverRegistry.Get(ds.Type);
        if (driver == null)
            return TestConnectionResultDto.Fail("unsupported_type",
                $"暂不支持的数据源类型: {ds.Type}。当前支持: {string.Join(", ", _driverRegistry.SupportedTypes)}");

        var result = await driver.TestAsync(ds, cancellationToken);

        await ToolSupport.SafeLogSqlAsync(_auditLogService, new SqlAuditLog
        {
            DataSourceId = ds.Id,
            DataSourceName = ds.Name,
            Operation = SqlOperation.Test,
            Sql = "-- connectivity test",
            Status = result.Success ? CommandStatus.Executed : CommandStatus.Failed,
            Result = result.Success ? result.Version : Truncate(result.Error, 500),
            DurationMs = result.DurationMs,
            Category = AuditCategory.Probe
        });

        return new TestConnectionResultDto
        {
            Success = result.Success,
            Status = result.Success ? null : "connection_error",
            Error = result.Success ? null : $"{result.Error}（请检查主机/端口/账号密码, 或该数据源绑定的 SSH 隧道服务器是否可用）",
            DatasourceId = ds.Id,
            Name = ds.Name,
            Host = ds.Host,
            Port = ds.Port,
            AccessMode = result.AccessMode,
            ViaTunnelServer = result.ViaTunnelServer,
            Version = result.Version,
            DurationMs = result.DurationMs
        };
    }

    [McpServerTool(Name = "datasource_get_sql_history", UseStructuredContent = true, OutputSchemaType = typeof(SqlHistoryDto), ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("查看SQL与Redis命令审计历史, 返回的result已截断到4000字符。支持limit/offset翻页; SSH命令历史用ssh_get_command_history")]
    public async Task<SqlHistoryDto> GetSqlHistory(
        [Description("数据源标识(可选, 留空查全部, 可用datasource_list列出)")] string? datasourceId = null,
        [Description(ToolSupport.HistoryLimitDescription)] int limit = ToolSupport.DefaultHistoryLimit,
        [Description("跳过的条数, 与limit配合翻页(默认0)")] int offset = 0,
        [Description("会话ID(可选): 只查某个 MCP 会话产生的记录; 当前会话ID见 mcp_self_check")] string? sessionId = null,
        [Description("工具名(可选): 只查由某个 MCP 工具产生的记录, 如 mysql_query / redis_execute")] string? tool = null,
        [Description("事件类型(可选): exec=执行 / gate=审批拦截 / probe=只读探测(测试/诊断/EXPLAIN)")] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadConfigAsync();
        string? filterId = null;
        if (!string.IsNullOrWhiteSpace(datasourceId))
        {
            var (ds, resolveStatus, resolveError) = ToolSupport.ResolveDatasource(config, datasourceId!);
            if (ds == null)
                return SqlHistoryDto.Fail(resolveStatus!, resolveError!);
            filterId = ds.Id;
        }

        var effectiveLimit = ToolSupport.ClampLimit(limit);
        var records = (await _auditLogService.GetSqlLogsAsync(
                filterId, effectiveLimit, null, false, Math.Max(0, offset), sessionId, tool,
                Enum.TryParse<AuditCategory>(category, ignoreCase: true, out var scat) ? scat : null))
            .ToList();

        foreach (var record in records)
        {
            if (record.Result != null && record.Result.Length > ToolSupport.MaxAuditResultChars)
                record.Result = record.Result[..ToolSupport.MaxAuditResultChars] + "...[已截断]";
        }

        return new SqlHistoryDto
        {
            Success = true,
            Count = records.Count,
            HasMore = records.Count >= effectiveLimit,
            Records = records
        };
    }

    private static string ResolveTunnelServerName(AppConfig config, DataSourceConfig ds)
    {
        if (!string.IsNullOrEmpty(ds.TunnelServerId))
            return config.Servers.FirstOrDefault(s => s.Id == ds.TunnelServerId)?.Name ?? ds.TunnelServerId;

        var edge = config.Relations.FirstOrDefault(r =>
            r.To == AssetNode.Ds(ds.Id) && r.From.StartsWith(AssetNode.SshPrefix, StringComparison.Ordinal));
        if (edge == null) return string.Empty;

        var serverId = edge.From[AssetNode.SshPrefix.Length..];
        return config.Servers.FirstOrDefault(s => s.Id == serverId)?.Name ?? serverId;
    }

    private static string LabelOf(AppConfig config, string nodeId)
    {
        if (nodeId.StartsWith(AssetNode.SshPrefix, StringComparison.Ordinal))
            return config.Servers.FirstOrDefault(s => s.Id == nodeId[4..])?.Name ?? nodeId;
        if (nodeId.StartsWith(AssetNode.DsPrefix, StringComparison.Ordinal))
            return config.DataSources.FirstOrDefault(d => d.Id == nodeId[3..])?.Name ?? nodeId;
        if (nodeId.StartsWith(AssetNode.AppPrefix, StringComparison.Ordinal))
            return config.Applications.FirstOrDefault(a => a.Id == nodeId[4..])?.Name ?? nodeId;
        return nodeId;
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value[..max] + "...");
}
