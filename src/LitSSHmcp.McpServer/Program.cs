using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Snapshot;
using LitSSHmcp.Core.Services.Snapshot.Collectors;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;
using LitSSHmcp.McpServer.Services;
using LitSSHmcp.McpServer.Resources;
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

// 操作用户归因（评估报告 D1 / §10.A.5）：身份只能由宿主 spawn 子进程时经环境变量注入，
// 子进程不自行决定、也不接受工具/前端参数（防伪造）。未注入 = 桌面未登录会话，归因 "local"。
var ownerUserId = Environment.GetEnvironmentVariable(HostEnv.OwnerUserId);
AuditContext.CurrentUserId = string.IsNullOrWhiteSpace(ownerUserId) ? "local" : ownerUserId.Trim();

// 会话工作目录（需求 D / §5.2-5.3）：同样只由宿主 spawn 经环境变量注入，
// 供 fileTransfer 条件白名单与工具级默认落地路径使用；未注入 = 旧会话/共享工作区。
SessionWorkspace.SetCurrent(Environment.GetEnvironmentVariable(HostEnv.SessionWorkspaceDir));

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
builder.Services.AddSingleton<ApprovalRoundTracker>();
builder.Services.AddSingleton<ApprovalExemptionRegistry>();
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
        // 脚本内容风险扫描(服务端权威): 上传/执行脚本前的规则分析, 报告附在审批弹窗
        builder.Services.AddSingleton<ScriptRiskAnalyzer>();
builder.Services.AddSingleton<ISqlFilterService, SqlFilterService>();
builder.Services.AddSingleton<IGuardedCommandService, GuardedCommandService>();
builder.Services.AddSingleton(new McpSessionTracker(sessionId));
builder.Services.AddSingleton<ISshKnownHostsStore, FileSshKnownHostsStore>();
// 限流跨进程共享（§4.3/M2-1）: App 与各 MCP 子进程在 ConfigPaths.TargetStateDir 上共享槽位/速率窗口
builder.Services.AddSingleton<ITargetLimiter>(sp => new TargetLimiter(
    sp.GetRequiredService<ISecurityOptionsProvider>(), ConfigPaths.TargetStateDir));
// 目标级写互斥（§4.3/M2-1）: 写类工具按目标跨进程串行, 读类自由并行
builder.Services.AddSingleton<ITargetWriteMutex>(_ => new FileTargetWriteMutex(ConfigPaths.TargetStateDir));

builder.Services.AddSingleton<IMySqlConnectionProvider, MySqlConnectionProvider>();
builder.Services.AddSingleton<IPostgresConnectionProvider, PostgresConnectionProvider>();
// Redis: 裸 provider 供"测试连接/健康检查"用（每次新建连接，反映真实连通性）；
// redis_read/redis_execute/redis_diagnostics 注入 RedisSessionCache 复用连接与 SSH 隧道
// （每调用新建连接时隧道模式多花 1~3s，且 SELECT 切库只对当次连接生效——下一调用又回默认库）。
builder.Services.AddSingleton<RedisConnectionProvider>();
builder.Services.AddSingleton<IRedisConnectionProvider>(sp => sp.GetRequiredService<RedisConnectionProvider>());
builder.Services.AddSingleton(sp => new RedisSessionCache(sp.GetRequiredService<RedisConnectionProvider>()));

// 数据源驱动: 新增数据源类型只需在此多注册一个 IDatasourceDriver 实现(驱动自报 Type),
// DatasourceDriverRegistry 以 IEnumerable<IDatasourceDriver> 注入并自动建立索引。
builder.Services.AddSingleton<IDatasourceDriver, MySqlDriver>();
builder.Services.AddSingleton<IDatasourceDriver, PostgresDriver>();
builder.Services.AddSingleton<IDatasourceDriver, RedisDriver>();
builder.Services.AddSingleton<IDatasourceDriverRegistry, DatasourceDriverRegistry>();
builder.Services.AddSingleton<ITopologyService, TopologyService>();
builder.Services.AddSingleton<IAssetGraphQueryService, AssetGraphQueryService>();

// 【同步约定 · 请勿删除】每个 WithTools<T>() 对应一个工具类。新增/移除工具类, 或工具类内的工具发生变动
// (新增/改名/删除、参数或描述变化)时, 必须同步以下三处, 否则 AI 客户端拿到的说明与实际能力不一致:
//   ① docs/TOOLS.md(工具说明的唯一事实来源, 含接入说明与意图路由表);
//   ② UsageGuideTools.GetUsageGuide() 内置工具清单;
//   ③ 桌面 App 菜单"配置 → MCP工具说明"(src/LitSSHmcp.App/Views/McpToolsWindow, 内容由 docs/TOOLS.md 嵌入)。
// 工具分组开关见 config.json 的 tools.enabledGroups(留空=全部), 分组与工具类映射见 docs/TOOLS.md「工具分组」。
var mcp = builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInstructions = McpServerInstructions.Build(enabledToolGroups);
        // 全局开关：security.enabled=false 时拒绝所有工具调用（热生效）
        options.Filters.Request.CallToolFilters.Add(McpGlobalSwitch.CreateFilter());
        // 会话跟踪：从 initialize 后的请求上下文取客户端信息写入会话表
        options.Filters.Request.CallToolFilters.Add(McpSessionFilter.CreateFilter());
        // 授权矩阵（M3-4 / §10.B.4）：Web 会话按"用户→目标白名单+操作级别"强制校验，
        // 放在写互斥之前（拒绝不占锁）、会话过滤器之后（审计可带上会话上下文）
        options.Filters.Request.CallToolFilters.Add(McpAuthorizationFilter.CreateFilter());
        // 目标级写互斥（§4.3/M2-1）：写类工具按目标跨进程串行，读类自由并行
        options.Filters.Request.CallToolFilters.Add(McpWriteGateFilter.CreateFilter());
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
        if (enabledToolGroups.Contains(ToolGroups.Topology)) mcp = mcp.WithTools<TopologyTools>().WithTools<GraphTools>();
if (enabledToolGroups.Contains(ToolGroups.App)) mcp = mcp.WithTools<AppTools>();
if (enabledToolGroups.Contains(ToolGroups.Guide)) mcp = mcp.WithTools<UsageGuideTools>().WithTools<HealthTools>();
// 内部工具：审批轮次边界。仅 LitSSH 宿主注入 LITSSH_INTERNAL_TOOLS=1 时注册，外部 MCP 客户端不可见。
if (HostEnv.InternalToolsEnabled)
    mcp = mcp.WithTools<ApprovalRoundTools>();

// MCP 资源：只读数据（供客户端“浏览/挂载”为上下文），按启用分组裁剪，与工具/说明保持一致。
if (enabledToolGroups.Contains(ToolGroups.Ssh)) mcp = mcp.WithResources<SshResources>().WithResources<SnapshotResources>();
if (enabledToolGroups.Contains(ToolGroups.Datasource)) mcp = mcp.WithResources<DatasourceResources>();
if (enabledToolGroups.Contains(ToolGroups.Topology)) mcp = mcp.WithResources<TopologyResources>();
if (enabledToolGroups.Contains(ToolGroups.Guide)) mcp = mcp.WithResources<GuideResources>();

builder.Logging.AddConsole(consoleLog =>
{
    consoleLog.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Logging.AddProvider(new FileLoggerProvider(ConfigPaths.LogsDir, retentionDays: 7));

var host = builder.Build();

// 审批弹窗标明所属会话：并行会话（每会话一个 MCP 子进程）后桌面会同时出现多个弹窗，
// 没有会话标识会导致审批错认（评估报告 §4.2）。
host.Services.GetRequiredService<DesktopApprovalService>().SessionLabel = sessionId;

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