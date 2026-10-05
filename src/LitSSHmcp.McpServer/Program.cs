using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Snapshot;
using LitSSHmcp.Core.Services.Snapshot.Collectors;
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

// 每次启动 MCP 服务生成一个会话 ID：不同 AI 客户端 / 重连（各自新起进程）会得到不同 ID，
// 审计记录据此区分"是哪个会话产生的"。格式便于按时间排序：yyyyMMdd-HHmmss-<8hex>。
var sessionId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";

// 读取工具分组配置(留空=全部)。在注册 MCP 工具前先算好, 以便只暴露启用的分组。
// 配置损坏/权限问题时不能让进程直接崩溃退出(客户端会收到一个无法解释的断连), 记下错误、按"全部启用"继续,
// 具体的配置错误会在 mcp_self_check / 各工具调用时以结构化错误返回。
AppConfig? startupConfig = null;
string? startupConfigError = null;
try
{
    startupConfig = await new ConfigService().LoadConfigAsync();
}
catch (Exception ex)
{
    startupConfigError = ex.Message;
}

var enabledToolGroups = ToolGroups.ResolveEnabled(startupConfig?.Tools);
var unknownToolGroups = ToolGroups.UnknownGroups(startupConfig?.Tools);

var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddSingleton<IConfigService, ConfigService>();
        builder.Services.AddSingleton<ISshConnectionPool>(sp => new SshConnectionPool(
            sp.GetService<ISshKnownHostsStore>(),
            () => sp.GetRequiredService<ISecurityOptionsProvider>().SshHostKey.Mode,
            () => sp.GetRequiredService<ISecurityOptionsProvider>().ConnectionPool));
        builder.Services.AddSingleton<ISshService, SshService>();
builder.Services.AddSingleton<IAuditLogService, AuditLogService>();
builder.Services.AddSingleton<DesktopApprovalService>();
builder.Services.AddSingleton<CliApprovalChannel>();
builder.Services.AddSingleton<IApprovalService, ApprovalService>();
builder.Services.AddSingleton<ITopologyStore, TopologyStore>();
builder.Services.AddSingleton<ISnapshotStore, SnapshotStore>();
builder.Services.AddSingleton<ISnapshotService, SnapshotService>();
// 快照采集维度(可插拔): 新增维度 = 实现 ISnapshotCollector 并在此多注册一行, 无需改快照主流程。
builder.Services.AddSingleton<ISnapshotCollector, ResourceSnapshotCollector>();
builder.Services.AddSingleton<ISnapshotCollector, PortMapSnapshotCollector>();
builder.Services.AddSingleton<ISnapshotCollector, DockerSnapshotCollector>();
builder.Services.AddSingleton<ISnapshotCollector, NginxTlsSnapshotCollector>();
builder.Services.AddSingleton<ISnapshotCollector, SystemdHealthSnapshotCollector>();
builder.Services.AddSingleton<ISnapshotCollector, SecurityAuditSnapshotCollector>();
builder.Services.AddSingleton<ISecurityOptionsProvider, SecurityOptionsProvider>();
builder.Services.AddSingleton<ICommandFilterService, CommandFilterService>();
builder.Services.AddSingleton<ISqlFilterService, SqlFilterService>();
builder.Services.AddSingleton<IGuardedCommandService, GuardedCommandService>();
builder.Services.AddSingleton(new McpSessionTracker(sessionId));
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
        // 会话跟踪：从 initialize 后的请求上下文取客户端信息写入会话表
        options.Filters.Request.CallToolFilters.Add(McpSessionFilter.CreateFilter());
    })
    .WithStdioServerTransport();

if (enabledToolGroups.Contains(ToolGroups.Ssh)) mcp = mcp.WithTools<ServerTools>().WithTools<SnapshotTools>();
if (enabledToolGroups.Contains(ToolGroups.Command)) mcp = mcp.WithTools<CommandTools>().WithTools<SudoTools>();
if (enabledToolGroups.Contains(ToolGroups.FileTransfer)) mcp = mcp.WithTools<FileTransferTools>();
if (enabledToolGroups.Contains(ToolGroups.Datasource)) mcp = mcp.WithTools<DatasourceTools>();
if (enabledToolGroups.Contains(ToolGroups.Mysql)) mcp = mcp.WithTools<MysqlTools>();
if (enabledToolGroups.Contains(ToolGroups.Postgres)) mcp = mcp.WithTools<PostgresTools>();
if (enabledToolGroups.Contains(ToolGroups.Redis)) mcp = mcp.WithTools<RedisTools>();
if (enabledToolGroups.Contains(ToolGroups.Docker)) mcp = mcp.WithTools<DockerTools>();
if (enabledToolGroups.Contains(ToolGroups.Service)) mcp = mcp.WithTools<ServiceTools>();
if (enabledToolGroups.Contains(ToolGroups.Log)) mcp = mcp.WithTools<LogTools>();
if (enabledToolGroups.Contains(ToolGroups.Java)) mcp = mcp.WithTools<JavaTools>();
if (enabledToolGroups.Contains(ToolGroups.Topology)) mcp = mcp.WithTools<TopologyTools>();
if (enabledToolGroups.Contains(ToolGroups.App)) mcp = mcp.WithTools<AppTools>();
if (enabledToolGroups.Contains(ToolGroups.Guide)) mcp = mcp.WithTools<UsageGuideTools>().WithTools<HealthTools>();

builder.Logging.AddConsole(consoleLog =>
{
    consoleLog.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Logging.AddProvider(new FileLoggerProvider(ConfigPaths.LogsDir, retentionDays: 7));

var host = builder.Build();

var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LitSSHmcp.Startup");
startupLogger.LogInformation("MCP 会话 ID: {SessionId}（审计记录按此区分；可用 mcp_self_check 查看）", sessionId);
if (startupConfigError is not null)
    startupLogger.LogError("启动时读取配置失败, 暂按全部工具分组启动, 工具调用会返回具体错误: {Error}", startupConfigError);
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

// 触发配置加载（含 schemaVersion 迁移）与各存储初始化。
// 单点失败不能让 MCP 进程在握手前退出：记录清晰错误后继续启动，
// 后续工具调用会通过 mcp_self_check / 结构化 status 暴露根因。
try
{
    var configService = host.Services.GetRequiredService<IConfigService>();
    await configService.LoadConfigAsync();

    var topologyStore = host.Services.GetRequiredService<ITopologyStore>();
    await topologyStore.InitializeAsync();

    var snapshotStore = host.Services.GetRequiredService<ISnapshotStore>();
    await snapshotStore.InitializeAsync();

    var auditLog = host.Services.GetRequiredService<IAuditLogService>();
    await auditLog.InitializeAsync();
    auditLog.SessionId = sessionId;

    // 启动即登记本次会话（客户端名称/版本会在首个工具调用时由过滤器回填）
    var sessionTracker = host.Services.GetRequiredService<McpSessionTracker>();
    await auditLog.RecordSessionAsync(sessionTracker.Snapshot());
}
catch (Exception ex)
{
    startupLogger.LogError(ex, "启动初始化失败, MCP 仍会启动; 请调用 mcp_self_check 查看各组件状态");
}

await host.RunAsync();
return 0;