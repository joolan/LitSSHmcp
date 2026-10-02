using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;
using LitSSHmcp.McpServer.Services;
using LitSSHmcp.McpServer.Tools;
using ModelContextProtocol.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (OperatingSystem.IsWindows())
{
    System.Windows.Forms.Application.EnableVisualStyles();
    System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
}

// 审批子进程模式：只显示审批对话框并按退出码回传结果，不启动 MCP 服务器
if (OperatingSystem.IsWindows() &&
    args.Length >= 2 &&
    args[0] == LitSSHmcp.McpServer.Services.ApprovalRequestHost.ArgumentName)
{
    return LitSSHmcp.McpServer.Services.ApprovalRequestHost.Run(args[1]);
}

// 读取工具分组配置(留空=全部)。在注册 MCP 工具前先算好, 以便只暴露启用的分组。
var startupConfig = await new ConfigService().LoadConfigAsync();
var enabledToolGroups = ToolGroups.ResolveEnabled(startupConfig.Tools);
var unknownToolGroups = ToolGroups.UnknownGroups(startupConfig.Tools);

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<IConfigService, ConfigService>();
builder.Services.AddSingleton<ISshService, SshService>();
builder.Services.AddSingleton<IAuditLogService, AuditLogService>();
builder.Services.AddSingleton<DesktopApprovalService>();
builder.Services.AddSingleton<CliApprovalChannel>();
builder.Services.AddSingleton<IApprovalService, ApprovalService>();
builder.Services.AddSingleton<ITopologyStore, TopologyStore>();
builder.Services.AddSingleton<ISecurityOptionsProvider, SecurityOptionsProvider>();
builder.Services.AddSingleton<ICommandFilterService, CommandFilterService>();
builder.Services.AddSingleton<ISqlFilterService, SqlFilterService>();
builder.Services.AddSingleton<ISshKnownHostsStore, FileSshKnownHostsStore>();
builder.Services.AddSingleton<ITargetLimiter, TargetLimiter>();

builder.Services.AddSingleton<IMySqlConnectionProvider, MySqlConnectionProvider>();
builder.Services.AddSingleton<IPostgresConnectionProvider, PostgresConnectionProvider>();
builder.Services.AddSingleton<IRedisConnectionProvider, RedisConnectionProvider>();

// 数据源驱动: 新增数据源类型只需在此多注册一个 IDatasourceDriver 实现(驱动自报 Type),
// DatasourceDriverRegistry 以 IEnumerable<IDatasourceDriver> 注入并自动建立索引。
builder.Services.AddSingleton<IDatasourceDriver, MySqlDriver>();
builder.Services.AddSingleton<IDatasourceDriver, PostgresDriver>();
builder.Services.AddSingleton<IDatasourceDriver, RedisDriver>();
builder.Services.AddSingleton<IDatasourceDriverRegistry, DatasourceDriverRegistry>();
builder.Services.AddSingleton<ITopologyService, TopologyService>();

// 【同步约定 · 请勿删除】每个 WithTools<T>() 对应一个工具类。新增/移除工具类, 或工具类内的工具发生变动
// (新增/改名/删除、参数或描述变化)时, 必须同步以下三处, 否则 AI 客户端拿到的说明与实际能力不一致:
//   ① docs/TOOLS.md(工具说明的唯一事实来源, 含接入说明与意图路由表);
//   ② UsageGuideTools.GetUsageGuide() 内置工具清单;
//   ③ 桌面 App 菜单"配置 → MCP工具说明"(src/LitSSHmcp.App/Views/McpToolsWindow, 内容由 docs/TOOLS.md 嵌入)。
// 工具分组开关见 config.json 的 tools.enabledGroups(留空=全部), 分组与工具类映射见 docs/TOOLS.md「工具分组」。
var mcp = builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInstructions = McpServerInstructions.Text;
        // 全局开关：security.enabled=false 时拒绝所有工具调用（热生效）
        options.Filters.Request.CallToolFilters.Add(McpGlobalSwitch.CreateFilter());
    })
    .WithStdioServerTransport();

if (enabledToolGroups.Contains(ToolGroups.Ssh)) mcp = mcp.WithTools<ServerTools>();
if (enabledToolGroups.Contains(ToolGroups.Command)) mcp = mcp.WithTools<CommandTools>().WithTools<SudoTools>();
if (enabledToolGroups.Contains(ToolGroups.FileTransfer)) mcp = mcp.WithTools<FileTransferTools>();
if (enabledToolGroups.Contains(ToolGroups.Datasource)) mcp = mcp.WithTools<DatasourceTools>();
if (enabledToolGroups.Contains(ToolGroups.Mysql)) mcp = mcp.WithTools<MysqlTools>();
if (enabledToolGroups.Contains(ToolGroups.Postgres)) mcp = mcp.WithTools<PostgresTools>();
if (enabledToolGroups.Contains(ToolGroups.Redis)) mcp = mcp.WithTools<RedisTools>();
if (enabledToolGroups.Contains(ToolGroups.Topology)) mcp = mcp.WithTools<TopologyTools>();
if (enabledToolGroups.Contains(ToolGroups.Guide)) mcp = mcp.WithTools<UsageGuideTools>().WithTools<HealthTools>();

builder.Logging.AddConsole(consoleLog =>
{
    consoleLog.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Logging.AddProvider(new FileLoggerProvider(ConfigPaths.LogsDir, retentionDays: 7));

var host = builder.Build();

var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LitSSHmcp.Startup");
startupLogger.LogInformation("MCP 工具分组: 启用 [{Enabled}]{Disabled}",
    string.Join(", ", ToolGroups.All.Where(enabledToolGroups.Contains)),
    enabledToolGroups.Count == ToolGroups.All.Length
        ? ""
        : "; 停用 [" + string.Join(", ", ToolGroups.All.Where(g => !enabledToolGroups.Contains(g))) + "]");
foreach (var unknown in unknownToolGroups)
    startupLogger.LogWarning("tools.enabledGroups 含未知分组 '{Group}', 已忽略 (可选: {Valid})",
        unknown, string.Join(", ", ToolGroups.All));
if (!enabledToolGroups.Contains(ToolGroups.Datasource) &&
    (enabledToolGroups.Contains(ToolGroups.Mysql) || enabledToolGroups.Contains(ToolGroups.Redis)))
    startupLogger.LogWarning("已停用 datasource 分组(datasource_list 等), 但启用了 mysql/redis 分组; AI 将无法列出数据源ID");

// 触发配置加载（含 schemaVersion 迁移）
var configService = host.Services.GetRequiredService<IConfigService>();
await configService.LoadConfigAsync();

var topologyStore = host.Services.GetRequiredService<ITopologyStore>();
await topologyStore.InitializeAsync();

var auditLog = host.Services.GetRequiredService<IAuditLogService>();
await auditLog.InitializeAsync();

await host.RunAsync();
return 0;