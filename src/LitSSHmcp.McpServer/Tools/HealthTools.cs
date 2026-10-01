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

    public HealthTools(
        IConfigService configService,
        IAuditLogService auditLogService,
        ISshKnownHostsStore knownHosts,
        ISshService sshService,
        IDatasourceDriverRegistry driverRegistry)
    {
        _configService = configService;
        _auditLogService = auditLogService;
        _knownHosts = knownHosts;
        _sshService = sshService;
        _driverRegistry = driverRegistry;
    }

    [McpServerTool(Name = "mcp_self_check", UseStructuredContent = true, OutputSchemaType = typeof(HealthCheckDto), ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("MCP自检: 配置/审计库/主机密钥可读写, 可选测服务器或数据源连通性。问'MCP是否正常/工具用不了'时用")]
    public async Task<HealthCheckDto> HealthCheck(
        [Description("可选: 要测试连通性的服务器ID, 可用ssh_list_servers列出")] string? serverId = null,
        [Description("可选: 要测试连通性的数据源ID, 可用datasource_list列出")] string? datasourceId = null)
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
            var server = config.Servers.FirstOrDefault(s => s.Id == serverId);
            if (server == null)
            {
                ok = false;
                checks.Add(new HealthCheckItemDto { Name = $"ssh:{serverId}", Status = "server_not_found", Detail = "服务器未找到" });
            }
            else
            {
                var reachable = await _sshService.TestConnectionAsync(server);
                if (!reachable) ok = false;
                checks.Add(new HealthCheckItemDto
                {
                    Name = $"ssh:{serverId}",
                    Status = reachable ? "ok" : "error",
                    Detail = reachable ? "连接成功" : "连接失败或主机密钥校验不通过"
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(datasourceId))
        {
            var config = await _configService.LoadConfigAsync();
            var ds = config.DataSources.FirstOrDefault(d => d.Id == datasourceId);
            if (ds == null)
            {
                ok = false;
                checks.Add(new HealthCheckItemDto { Name = $"ds:{datasourceId}", Status = "datasource_not_found", Detail = "数据源未找到" });
            }
            else
            {
                var driver = _driverRegistry.Get(ds.Type);
                if (driver == null)
                {
                    ok = false;
                    checks.Add(new HealthCheckItemDto { Name = $"ds:{datasourceId}", Status = "unsupported_type", Detail = ds.Type });
                }
                else
                {
                    var result = await driver.TestAsync(ds);
                    if (!result.Success) ok = false;
                    checks.Add(new HealthCheckItemDto
                    {
                        Name = $"ds:{datasourceId}",
                        Status = result.Success ? "ok" : "error",
                        Detail = result.Success
                            ? $"连接成功 ({result.DurationMs:F0}ms) {result.Version}" + (result.ViaTunnelServer != null ? $" 经隧道 {result.ViaTunnelServer}" : "")
                            : result.Error
                    });
                }
            }
        }

        return new HealthCheckDto { Success = ok, Checks = checks };
    }
}
