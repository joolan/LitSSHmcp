using System.Text.Json.Serialization;

namespace LitSSHmcp.Core.Models;

public class AppConfig
{
    public const int CurrentSchemaVersion = 4;

    /// <summary>配置结构版本。缺失该字段的旧文件反序列化为 0，由 ConfigMigrator 迁移到当前版本。</summary>
    public int SchemaVersion { get; set; }
    public SshServerConfig[] Servers { get; set; } = Array.Empty<SshServerConfig>();
    public DataSourceConfig[] DataSources { get; set; } = Array.Empty<DataSourceConfig>();
    public ApplicationConfig[] Applications { get; set; } = Array.Empty<ApplicationConfig>();
    public RelationConfig[] Relations { get; set; } = Array.Empty<RelationConfig>();
    public SecurityConfig Security { get; set; } = new();

    /// <summary>MCP 工具分组开关（留空 = 全部启用）。</summary>
    public ToolsConfig Tools { get; set; } = new();

    /// <summary>服务器快照（ssh_snapshot_get / ssh_snapshot_refresh）的行为配置。</summary>
    public SnapshotConfig Snapshot { get; set; } = new();

    /// <summary>SSH 连接复用池（每服务器复用一条连接、空闲自动断开，避免密集工具调用反复连断）。</summary>
    public ConnectionPoolConfig ConnectionPool { get; set; } = new();

    /// <summary>端口转发定义（本地/远程/动态 SOCKS）。</summary>
    public PortForwardConfig[] PortForwards { get; set; } = Array.Empty<PortForwardConfig>();

    /// <summary>远程桌面连接定义（RDP / VNC；可选经 SSH 隧道）。</summary>
    public RemoteDesktopConfig[] RemoteDesktops { get; set; } = Array.Empty<RemoteDesktopConfig>();

    /// <summary>界面外观（主题）。</summary>
    public UiConfig Ui { get; set; } = new();

    /// <summary>内置 AI 运维助手（LitSSHmcp.App 的 Agent）配置。</summary>
    public AgentConfig Agent { get; set; } = new();

    /// <summary>文件/文件夹同步任务（仅单向；App 运行期由调度器执行，关闭 App 即停止）。</summary>
    public SyncTaskConfig[] SyncTasks { get; set; } = Array.Empty<SyncTaskConfig>();

    /// <summary>触发事件（手动/定时/同步完成后按动作流水线执行 git、webhook、备份；App 运行期内生效）。</summary>
    public TriggerConfig[] Triggers { get; set; } = Array.Empty<TriggerConfig>();
}

/// <summary>
/// 内置 AI 运维助手配置：通过 OpenAI 兼容大模型对话，经内嵌 MCP 客户端调用本机 MCP 服务器完成运维。
/// </summary>
public class AgentConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>大模型接入列表（每个厂家/服务商一条，下挂多个可选模型）。</summary>
    public AgentProviderGroupConfig[] Providers { get; set; } = Array.Empty<AgentProviderGroupConfig>();

    /// <summary>当前选用的模型 Id（即某厂家下某模型的 Id；空=用第一个启用的）。</summary>
    public string ActiveProviderId { get; set; } = string.Empty;

    /// <summary>智能体列表（预置：chat/ops/db，均可编辑或删除；支持自定义新增）。</summary>
    public ChatAgentConfig[] Agents { get; set; } = ChatAgentConfig.Defaults();

    /// <summary>当前选用的智能体 Id（空=用列表第一个）。</summary>
    public string ActiveAgentId { get; set; } = string.Empty;

    /// <summary>追加在"基础行为约定 + MCP server instructions + 技能"之后的额外系统提示。</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>技能目录（含 *.md）；空=使用 App 内置的 litssh-mcp-ops-skill。</summary>
    public string SkillsDir { get; set; } = string.Empty;

    /// <summary>工作区目录（Agent 的文档读写工具 ops_doc_* 只能在此目录内读写）；空=默认「我的文档\LitSSH」。</summary>
    public string WorkspaceDir { get; set; } = string.Empty;

    /// <summary>保留最近 N 条消息（超出时丢弃最早的；0=不裁剪，仅受模型上限约束）。</summary>
    public int ContextLimit { get; set; } = 40;

    /// <summary>上下文 token 估算上限（与 ContextLimit 共同构成双阈值；超出时连同最旧整轮一起裁剪；0=仅按条数）。</summary>
    public int ContextTokenLimit { get; set; } = 96000;

    /// <summary>
    /// 冷启动 / 重载时，注入模型的**历史**只保留最近 N 轮（更早的由滚动摘要代表，降低冷启动首次请求成本）；
    /// 0=不限制（整段重建）。仅影响模型上下文，界面仍可翻看与导出全部记录。
    /// </summary>
    public int ColdStartRecentTurns { get; set; } = 30;

    /// <summary>滚动摘要：裁剪旧轮次时调用模型把其压缩成摘要并注入上下文（可选，默认开启）。</summary>
    public bool AutoSummarize { get; set; } = true;

    /// <summary>单个工具结果注入上下文的最大字符数（超出截断，完整内容仍见界面轨迹；0=不截断）。</summary>
    public int ToolResultMaxChars { get; set; } = 4000;

    /// <summary>提前触发比例：达到上限的该比例即开始裁剪/摘要（0.1~1，默认 0.8）。</summary>
    public double ContextTrimRatio { get; set; } = 0.8;

    /// <summary>发送给模型时精简 MCP 工具描述（截短为要点 + 指向 mcp_usage_guide，降低每次请求固定 token；默认开）。</summary>
    public bool CompactToolDescriptions { get; set; } = true;

    /// <summary>超大工具结果落盘为句柄（上下文只放预览 + spill:// 句柄，模型用 spill_read/spill_grep 按需读取；默认开）。</summary>
    public bool SpillLargeToolResults { get; set; } = true;

    /// <summary>大结果落盘目录；空=默认 %APPDATA%\LitSSH\spills。</summary>
    public string SpillDir { get; set; } = string.Empty;

    /// <summary>落盘结果保留天数（启动时清理更早的；0=不清理）。</summary>
    public int SpillRetentionDays { get; set; } = 7;

    /// <summary>启用子代理工具 run_subagent（把独立只读取证任务委派到隔离上下文，只回摘要；默认开）。</summary>
    public bool EnableSubAgent { get; set; } = true;

    /// <summary>回答风格：concise(默认, 只讲重点) / standard / detailed；影响系统提示的长度与展开要求。</summary>
    public string ResponseStyle { get; set; } = "concise";

    /// <summary>只读模式：仅向模型暴露非破坏性工具（ReadOnly）。</summary>
    public bool ReadOnly { get; set; }

    /// <summary>允许的工具分组（空=全部，仍受 tools.enabledGroups 约束）。</summary>
    public string[] AllowedToolGroups { get; set; } = Array.Empty<string>();

    /// <summary>长期记忆/RAG（Embeddings + 本地向量检索）配置。</summary>
    public AgentMemoryConfig Memory { get; set; } = new();

    /// <summary>MCP 服务器 exe 路径；空=自动探测（App 目录 mcp/LitSSHmcp.McpServer.exe / publish）。</summary>
    public string McpServerPath { get; set; } = string.Empty;

    /// <summary>单轮对话内工具调用循环的最大轮数（防失控）。</summary>
    public int MaxToolIterations { get; set; } = 20;

    /// <summary>会话保留：最多保留的会话数（0=不限）。</summary>
    public int RetentionMaxSessions { get; set; } = 50;

    /// <summary>会话保留：每个会话最多保留的消息条数（0=不限）。</summary>
    public int RetentionMaxMessages { get; set; } = 200;

    /// <summary>会话保留：超过该天数的会话自动清理（0=不限）。</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>本机 PowerShell 终端 & AI 本地 CLI 执行配置（默认关闭）。</summary>
    public LocalTerminalConfig LocalTerminal { get; set; } = new();
}

/// <summary>
/// 本机终端 / AI 本地 CLI 配置。安全策略：**硬白名单**（仅清单内 CLI 可执行，其余直接拒绝），
/// 且清单内命令默认每次人工审批（可「总是允许」记住）。默认关闭该工具组。
/// </summary>
public class LocalTerminalConfig
{
    /// <summary>是否向 AI 暴露本机 CLI 工具（local 工具组）。默认关闭。</summary>
    public bool Enabled { get; set; }

    /// <summary>指定 shell 可执行文件；空=自动（优先 pwsh，回退 powershell）。</summary>
    public string ShellPath { get; set; } = string.Empty;

    /// <summary>起始工作目录；空=用户主目录。</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>硬白名单：允许执行的 CLI（按可执行文件名匹配，忽略大小写与路径）。其余命令一律拒绝。</summary>
    public string[] AllowedCommands { get; set; } = DefaultAllowedCommands();

    /// <summary>已记住「总是允许」的完整命令（与原命令完全一致时跳过审批）。</summary>
    public string[] AutoApprovedCommands { get; set; } = Array.Empty<string>();

    /// <summary>本地安全硬上限（字符，超出保留尾部）。常规截断/落盘由中央 ToolResultMaxChars + spill 处理，这里只兜底极端输出。</summary>
    public int MaxOutputChars { get; set; } = 200_000;

    /// <summary>单条命令超时秒数。</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>开发/构建/云凭据类 CLI 的常用默认白名单。</summary>
    public static string[] DefaultAllowedCommands() => new[]
    {
        "git", "dotnet", "msbuild", "npm", "pnpm", "yarn", "node", "npx",
        "gh", "aws", "az", "terraform", "kubectl", "helm",
        "docker", "docker-compose", "compose",
        "python", "python3", "pip", "pip3", "poetry", "uv", "pdm", "conda",
        "go", "cargo", "rustc", "java", "mvn", "gradle", "make", "cmake",
        "where", "tree", "curl", "invoke-webrequest"
    };
}

/// <summary>
/// 一个大模型厂家/服务商接入（endpoint + API Key 厂家级共享，下挂多个可选模型）。
/// type=openai 表示 OpenAI 兼容端点。
/// </summary>
public class AgentProviderGroupConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "openai";
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>API Key（磁盘上为 DPAPI 密文 enc:）。</summary>
    public string? ApiKey { get; set; }

    public bool Enabled { get; set; } = true;
    public double Temperature { get; set; } = 0.3;

    /// <summary>最大输出 token；0=用服务端默认。</summary>
    public int MaxTokens { get; set; }

    /// <summary>单次请求超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>失败重试次数（网络/超时/限流/5xx 等瞬时错误）。</summary>
    public int MaxRetries { get; set; } = 1;

    /// <summary>同一模型允许的并发请求数上限。</summary>
    public int MaxConcurrency { get; set; } = 3;

    /// <summary>视觉能力的厂家级标记：v2 遗留值迁移到模型上；新增自定义模型时作为默认值。</summary>
    public bool SupportsVision { get; set; }

    /// <summary>该厂家下的模型列表（勾选启用的模型才会出现在模型下拉中）。</summary>
    public AgentModelConfig[] Models { get; set; } = Array.Empty<AgentModelConfig>();

    /// <summary>v2 单模型遗留字段：加载时由 ConfigMigrator 转入 Models 后置空（不再写出）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    /// <summary>
    /// 把厂家列表摊平为"可直接对话的模型"列表（供聊天/临时聊天下拉与会话选中使用）：
    /// 保留厂家级 endpoint/key/参数，模型级字段取自各模型；过滤掉停用的厂家/模型与空 endpoint/模型名。
    /// </summary>
    public static AgentProviderConfig[] Flatten(IEnumerable<AgentProviderGroupConfig>? groups)
    {
        if (groups is null)
            return Array.Empty<AgentProviderConfig>();

        var list = new List<AgentProviderConfig>();
        foreach (var g in groups)
        {
            if (g is null || !g.Enabled || string.IsNullOrWhiteSpace(g.Endpoint))
                continue;
            foreach (var m in g.Models ?? Array.Empty<AgentModelConfig>())
            {
                if (m is null || !m.Enabled || string.IsNullOrWhiteSpace(m.Name))
                    continue;
                list.Add(new AgentProviderConfig
                {
                    Id = m.Id,
                    Name = m.Name,
                    GroupName = g.Name,
                    Type = string.IsNullOrWhiteSpace(g.Type) ? "openai" : g.Type,
                    Endpoint = g.Endpoint,
                    Model = m.Name,
                    ApiKey = g.ApiKey,
                    Enabled = true,
                    Temperature = g.Temperature,
                    MaxTokens = g.MaxTokens,
                    TimeoutSeconds = g.TimeoutSeconds,
                    MaxRetries = g.MaxRetries,
                    MaxConcurrency = g.MaxConcurrency,
                    SupportsVision = m.SupportsVision,
                    InputPricePerMTok = m.InputPricePerMTok,
                    OutputPricePerMTok = m.OutputPricePerMTok
                });
            }
        }
        return list.ToArray();
    }
}

/// <summary>厂家下的单个模型条目（模型名 + 是否启用 + 是否支持视觉）。</summary>
public class AgentModelConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>模型名（OpenAI 兼容 model 参数，如 deepseek-chat / gpt-5-mini）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>是否启用（启用的模型才会出现在模型下拉中）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>该模型是否支持视觉（可接收图片附件；不同模型能力不同，逐模型配置）。</summary>
    public bool SupportsVision { get; set; }

    /// <summary>输入价格（每百万 token，用于成本估算；0=未填）。</summary>
    public double InputPricePerMTok { get; set; }

    /// <summary>输出价格（每百万 token，用于成本估算；0=未填）。</summary>
    public double OutputPricePerMTok { get; set; }
}

/// <summary>摊平后的单个可选模型（厂家参数 + 一个具体模型名），对话运行时直接消费。</summary>
public class AgentProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;

    /// <summary>所属厂家名（仅用于 UI 下拉分组显示）。</summary>
    public string GroupName { get; set; } = string.Empty;

    /// <summary>显示名："厂家 / 模型名"（厂家名为空时仅模型名），用于状态栏/提示。</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(GroupName) ? Name : $"{GroupName} / {Name}";

    public string Type { get; set; } = "openai";
    public string Endpoint { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;

    /// <summary>API Key（磁盘上为 DPAPI 密文 enc:）。</summary>
    public string? ApiKey { get; set; }

    public bool Enabled { get; set; } = true;
    public double Temperature { get; set; } = 0.3;

    /// <summary>最大输出 token；0=用服务端默认。</summary>
    public int MaxTokens { get; set; }

    /// <summary>单次请求超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>失败重试次数（网络/超时/限流/5xx 等瞬时错误）。</summary>
    public int MaxRetries { get; set; } = 1;

    /// <summary>同一模型允许的并发请求数上限。</summary>
    public int MaxConcurrency { get; set; } = 3;

    /// <summary>模型是否支持视觉（可接收图片附件）。</summary>
    public bool SupportsVision { get; set; }

    /// <summary>输入价格（每百万 token，用于成本估算；0=未填）。</summary>
    public double InputPricePerMTok { get; set; }

    /// <summary>输出价格（每百万 token，用于成本估算；0=未填）。</summary>
    public double OutputPricePerMTok { get; set; }
}

/// <summary>长期记忆：用 OpenAI 兼容 embeddings 端点把历史会话/工作区文档向量化并本地检索。</summary>
public class AgentMemoryConfig
{
    public bool Enabled { get; set; }

    public string Endpoint { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;

    /// <summary>Embeddings API Key（磁盘上为 DPAPI 密文 enc:）；留空则复用所选对话模型的 Key。</summary>
    public string? ApiKey { get; set; }

    /// <summary>每次召回的记忆条数。</summary>
    public int TopK { get; set; } = 5;
}

/// <summary>
/// 服务器快照配置。快照是"整机态势的可持久化采集结果"：
/// 资源使用/端口↔进程↔服务三元组/nginx证书/systemd健康，按服务器落库存历史。
/// </summary>
public class SnapshotConfig
{
    /// <summary>
    /// 采集是否自动提权(sudo)执行只读探测命令。开启后"端口↔进程↔服务"三元组能看到
    /// root 进程的属主与 cgroup 服务名；仅当该服务器配置了 SudoType 时才生效，
    /// 否则自动降级（相关字段缺失并标注 degraded）。逐次采集命令均为内置固定只读命令，
    /// 不要求逐次审批；把此项设为 false 可完全禁止快照提权。
    /// </summary>
    public bool UseSudo { get; set; } = true;

    /// <summary>每台服务器保留的最近快照份数（含其事件级联清理）；0 = 不限制。</summary>
    public int RetentionPerServer { get; set; } = 30;

    /// <summary>单次快照整体超时（秒），超时后剩余采集器标记为跳过并落库为失败。</summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>刷新最短间隔（秒）：距上次成功快照小于该值时，非 force 的刷新直接返回已有快照（0=不节流）。</summary>
    public int MinRefreshIntervalSeconds { get; set; } = 60;
}

public class UiConfig
{
    /// <summary>主题：system(默认, 跟随系统) / light / dark。</summary>
    public string Theme { get; set; } = "system";

    /// <summary>强调色（hex，如 #0078D4）；空=使用主题默认强调色。</summary>
    public string Accent { get; set; } = string.Empty;
}

public class ConnectionPoolConfig
{
    /// <summary>是否复用 SSH 连接（默认开）。关闭则每次调用新建连接、执行后断开。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>空闲多少秒后自动断开并释放连接（默认 300）。0=不自动断开。</summary>
    public int IdleTimeoutSeconds { get; set; } = 300;

    /// <summary>SSH 层 keepalive 间隔秒（默认 30，防止被 NAT/防火墙静默掐断）。0=关闭。</summary>
    public int KeepAliveSeconds { get; set; } = 30;

    /// <summary>建立连接超时秒（默认 20）。</summary>
    public int ConnectTimeoutSeconds { get; set; } = 20;

    /// <summary>每台服务器最多复用的连接数（默认 1；并发调用该服务器时分配到不同连接，1~16）。</summary>
    public int MaxPerServer { get; set; } = 1;
}

public class SecurityConfig
{
    /// <summary>全局 MCP 开关：false 时拒绝所有工具调用（安全设置中可切换，热生效、无需重启）。</summary>
    public bool Enabled { get; set; } = true;

    public CommandFilterConfig CommandFilter { get; set; } = new();
    public SqlFilterConfig SqlFilter { get; set; } = new();
    public FileTransferConfig FileTransfer { get; set; } = new();
    public SshHostKeyConfig SshHostKey { get; set; } = new();
    public DiscoveryConfig Discovery { get; set; } = new();

    /// <summary>日志读取工具(log_tail / log_grep)的允许路径与行数上限。</summary>
    public LogConfig Logs { get; set; } = new();

    public LimitsConfig Limits { get; set; } = new();
    public AuditConfig Audit { get; set; } = new();
    public ApprovalConfig Approval { get; set; } = new();

    /// <summary>查询结果列级脱敏规则。</summary>
    public MaskingConfig Masking { get; set; } = new();
}

/// <summary>结果列级脱敏配置：按列名正则匹配，命中即对单元格值脱敏。</summary>
public class MaskingConfig
{
    public MaskRule[] Rules { get; set; } = Array.Empty<MaskRule>();
}

public class MaskRule
{
    /// <summary>列名匹配（正则，忽略大小写），如 <c>phone|mobile</c>。</summary>
    public string Column { get; set; } = string.Empty;

    /// <summary>脱敏模式：full(全掩码 ***) / email / phone / last4。</summary>
    public string Mode { get; set; } = "full";
}

public class ApprovalConfig
{
    /// <summary>授权确认方式: dialog=自绘置顶对话框(带超时/美化); native=原生置顶 MessageBox(最稳)。</summary>
    public string Style { get; set; } = "dialog";

    /// <summary>
    /// 审批模式：
    ///  - <c>manual</c>(默认)：所有触发审批的敏感操作都需要人工处理（弹窗或带外 CLI）；
    ///  - <c>auto-approve</c>(危险)：所有触发审批的操作**自动放行**（不弹窗/不等带外），仅建议在受控/演示/自助环境使用；
    ///  - <c>auto-reject</c>：所有触发审批的操作**直接拒绝**（无人值守时默认拒绝的收敛策略）。
    /// 注意：仅影响"需人工确认"的敏感操作；被命令过滤器判为 <c>Blocked</c> 的仍然是硬拒绝，不受此开关影响。
    /// </summary>
    public string Mode { get; set; } = "manual";

    /// <summary>
    /// 审批通道(按顺序同时启用, 首个给出结论者生效):
    /// desktop=本机桌面弹窗; cli=带外 CLI/IPC(操作员用 litssh approve/deny 决定, 适配无桌面/headless)。
    /// 默认 desktop+cli：无桌面环境下 desktop 必然失败，只配 desktop 会导致这类环境 100% 拒绝。
    /// </summary>
    public string[] Channels { get; set; } = { "desktop", "cli" };

    /// <summary>
    /// 授权确认无操作超时（秒），超时自动拒绝并返回 <c>status=approval_timeout</c>；
    /// 0 表示不超时（按 300 秒兜底）。默认 45s：要短于多数 MCP 客户端的工具超时，
    /// 否则"等满 120s 再失败"会让整次调用被客户端判为超时。
    /// </summary>
    public int TimeoutSeconds { get; set; } = 45;

    /// <summary>弹窗是否强制置顶，避免被其它窗口遮挡。</summary>
    public bool TopMost { get; set; } = true;
}

public class AuditConfig
{
    /// <summary>是否记录 SQL 原文；关闭后审计仅记录占位符。</summary>
    public bool StoreSqlText { get; set; } = true;

    /// <summary>是否对 SQL 中的字符串/数字字面量脱敏（替换为 ?）。</summary>
    public bool MaskLiterals { get; set; } = false;

    /// <summary>审计记录保留天数，超期自动清理；0 表示不清理。</summary>
    public int RetentionDays { get; set; } = 90;
}

public enum SshHostKeyMode
{
    /// <summary>首次连接信任并记录指纹，后续指纹变化则拒绝（TOFU）。</summary>
    Tofu,
    /// <summary>只接受已记录的指纹，未记录也拒绝。</summary>
    Strict,
    /// <summary>不校验主机密钥（不推荐，仅兼容需要）。</summary>
    Off
}

public class SshHostKeyConfig
{
    public SshHostKeyMode Mode { get; set; } = SshHostKeyMode.Tofu;
}

public class DiscoveryConfig
{
    public string[] AllowedSearchPaths { get; set; } = { "/opt", "/home", "/srv", "/app", "/data", "/etc/nginx" };

    /// <summary>
    /// 自动发现是否用提权(sudo)执行只读探测命令。开启后 <c>ss -ltnp</c> 能看到 root 服务的进程名，
    /// 从而拿到精确监听端口（如宝塔启动的 nginx）；仅当该服务器配置了 SudoType 时才生效。
    /// 探测命令均为只读；提权密码由服务端注入、不会暴露给 AI。
    /// </summary>
    public bool UseSudo { get; set; }
}

/// <summary>
/// 日志读取工具(log_tail / log_grep)的约束：只允许读取白名单根目录下的日志文件，
/// 避免模型读取 /etc/shadow 等敏感文件；单次返回行数也有上限。
/// </summary>
public class LogConfig
{
    public string[] AllowedPaths { get; set; } = { "/var/log", "/opt", "/srv", "/app", "/data", "/home" };

    /// <summary>单次最多返回的行数（tool 参数上限的兜底）。</summary>
    public int MaxLines { get; set; } = 2000;
}

public class LimitsConfig
{
    /// <summary>同一目标（服务器/数据源）允许的并发调用数，超出即拒绝。</summary>
    public int MaxConcurrentPerTarget { get; set; } = 3;

    /// <summary>同一目标每分钟允许的调用数，超出即拒绝。</summary>
    public int MaxCallsPerMinutePerTarget { get; set; } = 60;

    /// <summary>数据库备份动作的最大并发数（同时运行的备份任务数，1~16）；防止多任务同时 dump 压垮本机/目标。</summary>
    public int MaxConcurrentBackups { get; set; } = 2;
}

public class FileTransferConfig
{
    public bool Enabled { get; set; } = true;
    public string[] AllowedLocalPaths { get; set; } = new[] { Environment.GetFolderPath(Environment.SpecialFolder.Desktop) };
    public string[] AllowedRemotePaths { get; set; } = new[] { "/home", "/tmp", "/var/log" };
    public long MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB
    public bool RequireApproval { get; set; } = true;
}

/// <summary>
/// 智能体：一套可切换的角色设定（系统提示附加段 + 可选回答风格覆盖）。
/// 只影响系统提示词**最末尾**的"当前智能体"段，不动工具集与稳定前缀（保持 prompt 缓存命中）。
/// </summary>
public class ChatAgentConfig
{
    /// <summary>稳定 Id（预置为 chat/ops/db；自定义用 GUID）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>系统提示附加段（追加到"## 当前智能体"下；为空=仅显示名称）。</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>预置智能体（允许重置/不可彻底丢失；UI 标记"预置"）。自定义=false。</summary>
    public bool Builtin { get; set; }

    /// <summary>回答风格覆盖：concise / standard / detailed；空=跟随全局 agent.responseStyle。</summary>
    public string? ResponseStyle { get; set; }

    /// <summary>运行期注入系统提示的智能体上下文（非配置持久化项）。</summary>
    public AgentPromptContext ToPromptContext() => new(Name, SystemPrompt, ResponseStyle);

    /// <summary>三个预置智能体（chat/ops/db）。JSON 缺 Agents 字段时由属性初始化器补齐。</summary>
    public static ChatAgentConfig[] Defaults() => new[]
    {
        new ChatAgentConfig
        {
            Id = "chat", Name = "通用聊天", Builtin = true,
            Description = "日常问答与轻量运维咨询，跟随全局设置",
            SystemPrompt = string.Empty,
        },
        new ChatAgentConfig
        {
            Id = "ops", Name = "运维专家", Builtin = true,
            Description = "服务器排障与变更操作：严格按'只读取证→定位→最小变更→验证'流程，变更前说明影响与回滚",
            SystemPrompt = "你是资深 Linux/SRE 运维专家。处理故障时先只读取证(快照/日志/进程/端口)、给出根因假设与验证步骤，再提出最小变更方案并说明影响与回滚方式；变更后必须做验证。",
        },
        new ChatAgentConfig
        {
            Id = "db", Name = "数据库分析", Builtin = true,
            Description = "SQL/慢查询/性能分析：只做只读分析，写操作需用户明确要求",
            SystemPrompt = "你是数据库性能分析专家。分析 SQL/慢查询/执行计划时先给出结论与证据(关键指标+采样片段)，再给优化建议(含索引/改写/架构层面)；默认只做只读分析，任何写操作(改表/改数据/加索引)必须由用户明确要求并经审批。",
        },
    };
}

/// <summary>注入系统提示的智能体上下文（AgentRuntime 消费；非持久化）。</summary>
public sealed record AgentPromptContext(string Name, string SystemPrompt, string? ResponseStyle);