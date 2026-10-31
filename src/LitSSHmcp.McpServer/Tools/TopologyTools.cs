// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + mcp_usage_guide 内置清单(由注解反射生成, 无需手改);
//   ③ 若新增了工具类, 记得在 Program.cs 注册 WithTools<T>()。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。详见 docs/TOOLS.md 顶部"同步约定"。
using System.ComponentModel;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

[McpServerToolType]
public class TopologyTools
{
    private readonly ITopologyService _topologyService;
    private readonly IConfigService _configService;
    private readonly ICommandFilterService _commandFilter;
    private readonly IApprovalService _approvalService;
    private readonly IAuditLogService _auditLogService;
    private readonly ISecurityOptionsProvider _securityOptions;

    public TopologyTools(
        ITopologyService topologyService,
        IConfigService configService,
        ICommandFilterService commandFilter,
        IApprovalService approvalService,
        IAuditLogService auditLogService,
        ISecurityOptionsProvider securityOptions)
    {
        _topologyService = topologyService;
        _configService = configService;
        _commandFilter = commandFilter;
        _approvalService = approvalService;
        _auditLogService = auditLogService;
        _securityOptions = securityOptions;
    }

    [McpServerTool(Name = "topology_discover", UseStructuredContent = true, OutputSchemaType = typeof(DiscoverResultDto), Destructive = false, ReadOnly = false, Idempotent = false, OpenWorld = true)]
    [Description("采集服务器发现证据(java进程/端口/服务/容器/JDBC配置/processlist)；只返回证据与配置建议，不修改人工配置拓扑图，通常10-60秒/台")]
    public async Task<DiscoverResultDto> DiscoverTopology(
        [Description("限定扫描的服务器ID或名称, 逗号分隔; 留空扫描全部")] string? serverIds = null,
        [Description("配置文件搜索路径, 空格分隔; 留空使用配置中允许的路径(security.discovery.allowedSearchPaths)")] string? searchPaths = null,
        CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadConfigAsync();
        var allowedPaths = config.Security.Discovery.AllowedSearchPaths ?? Array.Empty<string>();

        var servers = Split(serverIds);
        var requestedPaths = Split(searchPaths, separator: ' ');

        string[]? paths;
        if (requestedPaths == null || requestedPaths.Length == 0)
        {
            paths = allowedPaths;
        }
        else
        {
            var invalid = requestedPaths.Where(p => !PathPolicy.IsRemotePathAllowed(p, allowedPaths)).ToArray();
            if (invalid.Length > 0)
            {
                return DiscoverResultDto.Fail("path_not_allowed",
                    $"以下扫描路径不在允许范围: {string.Join(", ", invalid)}。允许路径: {string.Join(", ", allowedPaths)}");
            }

            paths = requestedPaths;
        }

        // 扫描路径会被拼进远端 shell 命令：先过命令过滤（阻断/敏感），敏感时需人工确认。
        // 路径本身在 TopologyService 内统一做单引号转义，这里负责策略与审批门禁。
        if (paths is { Length: > 0 })
        {
            var probeCommand = TopologyService.BuildConfigScanCommand(paths);
            var verdict = _commandFilter.CheckCommand(probeCommand);

            if (verdict == CommandFilterResult.Blocked)
            {
                await LogAuditAsync(probeCommand, "blocked", CommandStatus.Blocked, AuditCategory.Gate, "blocked");
                return DiscoverResultDto.Fail("blocked",
                    "扫描路径被安全策略拦截(路径中含被禁止的命令片段)。请改用不带特殊字符的目录路径。");
            }

            if (verdict == CommandFilterResult.Sensitive)
            {
                var outcome = await _approvalService.RequestApprovalAsync(
                    "topology_discover", probeCommand, verdict, null, "拓扑扫描", cancellationToken);
                if (outcome != ApprovalOutcome.Approved)
                {
                    await LogAuditAsync(probeCommand, null, CommandStatus.Rejected, AuditCategory.Gate, ToolSupport.DecisionFor(outcome, _securityOptions.Approval.Mode));
                    var (status, error) = ApprovalOutcomeText.Describe(outcome, _securityOptions.Approval.TimeoutSeconds);
                    return DiscoverResultDto.Fail(status, error);
                }
                await LogAuditAsync(probeCommand, null, CommandStatus.Approved, AuditCategory.Gate, ToolSupport.DecisionFor(outcome, _securityOptions.Approval.Mode));
            }
        }

        try
        {
            var result = await _topologyService.DiscoverAsync(servers, paths, cancellationToken);
            await LogAuditAsync("TOPOLOGY_DISCOVER", $"证据边 {result.NewEdges.Length} / 错误 {result.Errors.Length}", CommandStatus.Executed, AuditCategory.Probe);
            return new DiscoverResultDto { Success = true, Result = result };
        }
        catch (DiscoveryInProgressException)
        {
            return DiscoverResultDto.Fail("discovery_in_progress",
                "已有自动发现在执行，请稍后重试（本次已跳过，避免并发扫描；可先用 topology_query(action=overview) 查看人工主图）。");
        }
    }

    private Task LogAuditAsync(string command, string? result, CommandStatus status, AuditCategory category, string? decision = null) =>
        ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
        {
            ServerId = "topology",
            ServerName = "topology",
            Command = command,
            Result = result,
            Status = status,
            Category = category,
            Decision = decision
        });

    private static string[]? Split(string? value, char separator = ',')
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts;
    }
}
