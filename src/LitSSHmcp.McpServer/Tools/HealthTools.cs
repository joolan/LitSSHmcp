// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + get_usage_guide 内置清单(由注解反射生成, 无需手改);
//   ③ 若新增了工具类, 记得在 Program.cs 注册 WithTools<T>()。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。详见 docs/TOOLS.md 顶部"同步约定"。
using System.ComponentModel;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

[McpServerToolType]
public class HealthTools
{
    private readonly IConfigService _configService;
    private readonly IAuditLogService _auditLogService;
    private readonly ISshKnownHostsStore _knownHosts;
    private readonly ISshService _sshService;
    private readonly IDatasourceDriverRegistry _driverRegistry;
    private readonly McpSessionTracker _sessionTracker;

    public HealthTools(
        IConfigService configService,
        IAuditLogService auditLogService,
        ISshKnownHostsStore knownHosts,
        ISshService sshService,
        IDatasourceDriverRegistry driverRegistry,
        McpSessionTracker sessionTracker)
    {
        _configService = configService;
        _auditLogService = auditLogService;
        _knownHosts = knownHosts;
        _sshService = sshService;
        _driverRegistry = driverRegistry;
        _sessionTracker = sessionTracker;
    }

    [McpServerTool(Name = "mcp_self_check", UseStructuredContent = true, OutputSchemaType = typeof(HealthCheckDto), ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("MCP自检: 配置/审计库/主机密钥可读写, 可选测某服务器或数据源连通性。问'MCP是否正常/工具用不了'时用")]
    public async Task<HealthCheckDto> HealthCheck(
        [Description("可选: 要测试连通性的服务器标识(ID/名称/主机名), 可用ssh_list_servers列出")] string? serverId = null,
        [Description("可选: 要测试连通性的数据源标识(ID/名称), 可用datasource_list列出")] string? datasourceId = null,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<HealthCheckItemDto>();
        var ok = true;

        try
        {
            var config = await _configService.LoadConfigAsync();
            checks.Add(new HealthCheckItemDto
            {
                Name = "config",
                Status = "ok",
                Detail = $"servers={config.Servers.Length}, dataSources={config.DataSources.Length}, applications={config.Applications.Length}, relations={config.Relations.Length}"
            });
        }
        catch (Exception ex)
        {
            ok = false;
            checks.Add(new HealthCheckItemDto { Name = "config", Status = "error", Detail = ex.Message });
        }

        try
        {
            await _auditLogService.InitializeAsync();
            checks.Add(new HealthCheckItemDto { Name = "audit_db", Status = "ok", Detail = "可读写" });
        }
        catch (Exception ex)
        {
            ok = false;
            checks.Add(new HealthCheckItemDto { Name = "audit_db", Status = "error", Detail = ex.Message });
        }

        try
        {
            var count = _knownHosts.GetAll().Count;
            checks.Add(new HealthCheckItemDto { Name = "known_hosts", Status = "ok", Detail = $"已记录 {count} 个主机密钥" });
        }
        catch (Exception ex)
        {
            ok = false;
            checks.Add(new HealthCheckItemDto { Name = "known_hosts", Status = "error", Detail = ex.Message });
        }

        if (!string.IsNullOrWhiteSpace(serverId))
        {
            var config = await _configService.LoadConfigAsync();
            var (server, resolveStatus, resolveError) = ToolSupport.ResolveServer(config, serverId!);
            if (server == null)
            {
                ok = false;
                checks.Add(new HealthCheckItemDto { Name = $"ssh:{serverId}", Status = resolveStatus ?? "server_not_found", Detail = resolveError ?? string.Empty });
            }
            else
            {
                var probe = await _sshService.ProbeConnectionAsync(server, cancellationToken);
                if (!probe.Success) ok = false;
                checks.Add(new HealthCheckItemDto
                {
                    Name = $"ssh:{server.Name} ({server.Host})",
                    Status = probe.Success ? "ok" : ToolSupport.ConnectionFailureStatus(probe.ErrorKind),
                    Detail = probe.Success
                        ? $"连接成功 ({probe.Duration.TotalMilliseconds:F0}ms)"
                        : $"{probe.Error} [kind={probe.ErrorKind}]"
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(datasourceId))
        {
            var config = await _configService.LoadConfigAsync();
            var (ds, resolveStatus, resolveError) = ToolSupport.ResolveDatasource(config, datasourceId!);
            if (ds == null)
            {
                ok = false;
                checks.Add(new HealthCheckItemDto { Name = $"ds:{datasourceId}", Status = resolveStatus ?? "datasource_not_found", Detail = resolveError ?? string.Empty });
            }
            else
            {
                var driver = _driverRegistry.Get(ds.Type);
                if (driver == null)
                {
                    ok = false;
                    checks.Add(new HealthCheckItemDto
                    {
                        Name = $"ds:{ds.Name} ({ds.Host}:{ds.Port})",
                        Status = "unsupported_type",
                        Detail = $"类型 {ds.Type} 暂不支持, 支持: {string.Join(", ", _driverRegistry.SupportedTypes)}"
                    });
                }
                else
                {
                    var result = await driver.TestAsync(ds, cancellationToken);
                    if (!result.Success) ok = false;
                    checks.Add(new HealthCheckItemDto
                    {
                        Name = $"ds:{ds.Name} ({ds.Host}:{ds.Port})",
                        Status = result.Success ? "ok" : "connection_error",
                        Detail = result.Success
                            ? $"连接成功 ({result.DurationMs:F0}ms) {result.Version}" + (result.ViaTunnelServer != null ? $" 经隧道 {result.ViaTunnelServer}" : "")
                            : result.Error
                    });
                }
            }
        }

        return new HealthCheckDto
        {
            Success = ok,
            SessionId = _auditLogService.SessionId,
            ClientName = _sessionTracker.ClientName,
            ClientVersion = _sessionTracker.ClientVersion,
            Checks = checks
        };
    }

    [McpServerTool(Name = "mcp_list_sessions", UseStructuredContent = true, OutputSchemaType = typeof(SessionListDto), ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("列出最近的 MCP 会话(会话ID/客户端名称与版本/首末活动时间)。审计记录带 sessionId, 用它能知道是哪个AI客户端、哪次连接产生的; 配合 *_history 工具的 sessionId 参数过滤具体记录")]
    public async Task<SessionListDto> ListSessions(
        [Description("返回条数(默认50, 上限500)")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sessions = await _auditLogService.GetSessionsAsync(Math.Clamp(limit, 1, 500));
            return new SessionListDto { Success = true, Count = sessions.Length, Sessions = sessions.ToList() };
        }
        catch (Exception ex)
        {
            return SessionListDto.Fail(ex.Message);
        }
    }
}
