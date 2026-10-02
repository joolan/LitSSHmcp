// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + get_usage_guide 内置清单(由注解反射生成, 无需手改);
//   ③ 若新增了工具类, 记得在 Program.cs 注册 WithTools<T>()。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。详见 docs/TOOLS.md 顶部"同步约定"。
using System.ComponentModel;
using System.Text.RegularExpressions;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

[McpServerToolType]
public class AppTools
{
    private readonly IConfigService _configService;
    private readonly IGuardedCommandService _runner;
    private readonly IDatasourceDriverRegistry _driverRegistry;

    public AppTools(IConfigService configService, IGuardedCommandService runner, IDatasourceDriverRegistry driverRegistry)
    {
        _configService = configService;
        _runner = runner;
        _driverRegistry = driverRegistry;
    }

    /// <summary>按应用拼装探测命令：java 按应用名过滤、docker 按容器名过滤、端口按应用端口过滤（缺省则该段列全部）。</summary>
    private static string BuildProbeCommand(ApplicationConfig app)
    {
        var java = "ps -eo pid,etime,%cpu,%mem,args --no-headers | grep '[j]ava'";
        if (!string.IsNullOrWhiteSpace(app.Name))
            java += $" | grep -i -E -- {ShellQuote.Single(Regex.Escape(app.Name))}";

        var docker = !string.IsNullOrWhiteSpace(app.ContainerName)
            ? $"docker ps -a --filter name={ShellQuote.Single(app.ContainerName)} --format '{{{{.Names}}}} | {{{{.Status}}}} | {{{{.Image}}}}' 2>/dev/null"
            : "docker ps --format '{{.Names}} | {{.Status}} | {{.Image}}' 2>/dev/null";

        var listen = app.Port is int p and > 0
            ? $"(ss -ltnp 2>/dev/null || netstat -ltnp 2>/dev/null) | grep -E -- ':{p}([^0-9]|$)'"
            : "(ss -ltnp 2>/dev/null || netstat -ltnp 2>/dev/null)";

        return $"echo '## java'; {java} | head -20; " +
               $"echo '## docker'; {docker} | head -50; " +
               $"echo '## listen'; {listen} | head -50";
    }

    [McpServerTool(Name = "app_health_snapshot", UseStructuredContent = true, OutputSchemaType = typeof(AppHealthSnapshotDto), ReadOnly = true, OpenWorld = true)]
    [Description("按应用聚合一键体检快照: 所在服务器的Java进程/容器/监听端口(按应用名/容器名/端口过滤) + 依赖数据源的连通性(可选诊断)。跨机排查某应用时先用它, 再按需下钻到具体工具")]
    public async Task<AppHealthSnapshotDto> AppHealthSnapshot(
        [Description("应用标识: ID或名称, 可在拓扑总览里查看, 可用topology_get_overview列出")] string appId,
        [Description("true=对依赖数据源额外做整体诊断(更慢更全); 默认只测连通性")] bool includeDiagnostics = false,
        CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadConfigAsync();
        var (app, appStatus, appError) = ToolSupport.ResolveApplication(config, appId);
        if (app == null)
            return AppHealthSnapshotDto.Fail(appStatus!, appError!);

        var appNode = AssetNode.App(app.Id);
        var notes = new List<string>
        {
            $"服务器探测过滤: java 按名称 '{app.Name}'" +
            (string.IsNullOrWhiteSpace(app.ContainerName) ? ", docker 列全部" : $", docker 按容器 '{app.ContainerName}'") +
            (app.Port is int ? $", 端口 {app.Port}" : ", 端口列全部")
        };

        // —— 关联服务器：优先取 runsOn 关系，其次用应用 host 解析 ——
        var serverIds = config.Relations
            .Where(r => r.From == appNode && r.To.StartsWith(AssetNode.SshPrefix, StringComparison.Ordinal))
            .Select(r => r.To[AssetNode.SshPrefix.Length..])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (serverIds.Count == 0 && !string.IsNullOrWhiteSpace(app.Host))
        {
            var (byHost, hostStatus, hostError) = ToolSupport.ResolveServer(config, app.Host);
            if (byHost != null)
                serverIds.Add(byHost.Id);
            else if (hostStatus == "server_ambiguous")
                notes.Add(hostError!);
        }

        if (serverIds.Count == 0)
            notes.Add("未找到该应用关联的服务器(runsOn 关系)；可在资产关系编辑器补充, 或运行 topology_discover 自动发现。");

        var servers = new List<AppServerHealthDto>();
        foreach (var sid in serverIds)
        {
            var server = config.Servers.FirstOrDefault(s => string.Equals(s.Id, sid, StringComparison.OrdinalIgnoreCase));
            if (server == null)
            {
                servers.Add(new AppServerHealthDto { ServerId = sid, Reachable = false, Status = "server_not_found", Error = $"服务器 {sid} 不在配置中" });
                continue;
            }

            var outcome = await _runner.RunAsync(server.Id, BuildProbeCommand(app), cancellationToken);
            servers.Add(new AppServerHealthDto
            {
                ServerId = server.Id,
                ServerName = server.Name,
                Host = server.Host,
                Reachable = outcome.Success,
                Status = outcome.Status,
                Error = outcome.Error,
                Output = outcome.Output,
                Truncated = outcome.Truncated
            });
        }

        // —— 关联数据源：connectsTo 关系 ——
        var dsIds = config.Relations
            .Where(r => r.From == appNode && r.To.StartsWith(AssetNode.DsPrefix, StringComparison.Ordinal))
            .Select(r => r.To[AssetNode.DsPrefix.Length..])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (dsIds.Count == 0)
            notes.Add("未找到该应用关联的数据源(connectsTo 关系)。");

        var datasources = new List<AppDatasourceHealthDto>();
        foreach (var did in dsIds)
        {
            var ds = config.DataSources.FirstOrDefault(d => string.Equals(d.Id, did, StringComparison.OrdinalIgnoreCase));
            if (ds == null)
            {
                datasources.Add(new AppDatasourceHealthDto { DatasourceId = did, Reachable = false, Status = "datasource_not_found", Error = $"数据源 {did} 不在配置中" });
                continue;
            }

            var driver = _driverRegistry.Get(ds.Type);
            if (driver == null)
            {
                datasources.Add(new AppDatasourceHealthDto { DatasourceId = ds.Id, Name = ds.Name, Type = ds.Type, Reachable = false, Status = "unsupported_type", Error = $"类型 {ds.Type} 暂不支持" });
                continue;
            }

            var test = await driver.TestAsync(ds, cancellationToken);
            var dto = new AppDatasourceHealthDto
            {
                DatasourceId = ds.Id,
                Name = ds.Name,
                Type = ds.Type,
                Reachable = test.Success,
                Status = test.Success ? null : "connection_error",
                Version = test.Version,
                AccessMode = test.AccessMode,
                ViaTunnelServer = test.ViaTunnelServer,
                Error = test.Success ? null : test.Error
            };

            if (test.Success && includeDiagnostics)
            {
                try
                {
                    var diag = await driver.DiagnoseAsync(ds, cancellationToken);
                    if (diag.Success)
                        dto.Summary = diag.Summary;
                    else
                        dto.Error = diag.Error;
                }
                catch (Exception ex)
                {
                    dto.Error = ex.Message;
                }
            }

            datasources.Add(dto);
        }

        return new AppHealthSnapshotDto
        {
            Success = true,
            Application = new AppInfoDto
            {
                Id = app.Id,
                Name = app.Name,
                Type = app.Type,
                ContainerName = app.ContainerName,
                Port = app.Port
            },
            Servers = servers,
            Datasources = datasources,
            Notes = notes
        };
    }
}
