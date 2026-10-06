# LitSSH MCP 架构设计

本文描述项目的分层结构、核心模块、安全模型与开发约定，供后续迭代或他人接手时参考。

## 1. 总览

LitSSH MCP 是一个运行在 Windows 本机的 MCP(Model Context Protocol) 服务器，通过 stdio 传输为 AI 客户端提供三类能力：

1. **SSH 侧**：在远程 Linux 服务器上执行命令（含 sudo 提权）、上传/下载文件、浏览目录
2. **数据库侧**：连接配置好的 MySQL / Redis（直连或经 SSH 隧道），执行只读查询/写操作/诊断
3. **拓扑侧**：维护"服务器 / Java应用 / 数据库"资产拓扑，支撑跨机全链路故障排查

三个独立入口共享同一套 Core：

```
┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────────┐
│ LitSSHmcp.McpServer │  │   LitSSHmcp.App     │  │   LitSSHmcp.Cli     │
│ MCP服务器(stdio)     │  │ WPF管理界面(MVVM)    │  │ 终端SSH工具          │
 │ 51个MCP Tools        │  │ 服务器/数据源/拓扑配置 │  │ list/connect/run    │
└──────────┬──────────┘  └──────────┬──────────┘  └──────────┬──────────┘
           │                        │                        │
           └────────────────────────┼────────────────────────┘
                                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│                       LitSSHmcp.Core                               │
│  Models: AppConfig/SshServerConfig/DataSourceConfig/TopologyModels  │
│  Services: SSH | Datasource | Topology | Security | Storage         │
└─────────────────────────────────────────────────────────────────────┘
                                    │
                    ┌───────────────┼───────────────┐
                    ▼               ▼               ▼
             %APPDATA%\LitSSH   SQLite(audit.db)  远程资产
             config.json        审计/拓扑/快照       SSH服务器/MySQL/PostgreSQL/Redis
```

## 2. 解决方案结构

| 项目 | 目标框架 | 职责 | 关键依赖 |
|------|---------|------|---------|
| `LitSSHmcp.Core` | net8.0 | 数据模型 + 全部业务服务（不含任何入口逻辑） | SSH.NET 2026.0.0、MySqlConnector 2.4.0、Npgsql 8.0.5、Microsoft.Data.Sqlite、System.Security.Cryptography.ProtectedData |
| `LitSSHmcp.McpServer` | net8.0-windows | MCP 服务器入口（stdio 传输）、53 个工具、授权确认弹窗（WinForms） | ModelContextProtocol 2.2.0、Microsoft.Extensions.Hosting、WinForms |
| `LitSSHmcp.App` | net8.0-windows | WPF 管理界面（服务器/数据源/应用/资产拓扑可视化编辑/安全设置/审计/工具说明/工具分组） | WPF、Core |
| `LitSSHmcp.Cli` | net10.0 | 终端 SSH 工具（`litssh list/connect/run`） | Core |
| `LitSSHmcp.Tests` | net8.0 | Core 单元测试（安全过滤/路径策略/配置迁移/加密/驱动协议等） | Core、xunit |
| `LitSSHmcp.McpServer.Tests` | net8.0-windows | MCP 服务器层测试：工具清单一致性（注册 ↔ `docs/TOOLS.md` ↔ 指南）+ **stdio 全链路集成测试**（initialize/tools/list/tools/call） | McpServer、xunit |
| `LitSSHmcp.App.Tests` | net8.0-windows | App 画布测试：拓扑路由引擎（Z 形/避障/快慢一致/过桥/简化）、布局几何与四边缩放、导出渲染（STA） | App、xunit |

- 依赖方向：三个入口项目都只依赖 `Core`，`Core` 不反向依赖任何入口项目；`tests` 项目依赖 `Core`（`LitSSHmcp.App.Tests` 另依赖 `App`）。
- 构建环境：`.NET SDK 10.0.x` 可构建全部项目（入口运行时目标仍为 .NET 8，CLI 为 .NET 10）。
- 代码约定：**接口与实现放在同一文件**（如 `IConfigService.cs` 内含 `ConfigService`），接口以 `I` 前缀命名。

仓库目录：

```
LitSSHmcp/
├── LitSSHmcp.slnx
├── README.md                     # 项目门面
├── docs/                         # 文档（本目录）
│   ├── ARCHITECTURE.md           # 架构设计
│   ├── TOOLS.md                  # 工具参考与意图路由
│   ├── CHANGELOG.md              # 迭代历史
│   └── UPGRADE_PLAN.md           # 升级方案（Roadmap）
├── config/
│   └── config.example.json       # 配置文件示例
├── src/
│   ├── LitSSHmcp.Core/           # 模型 + 业务服务（SSH/Datasource/Topology/Security/Storage）
│   ├── LitSSHmcp.McpServer/      # MCP 服务器（Program.cs + Tools/ 17 个工具类）
│   ├── LitSSHmcp.Agent/          # AI 运维助手（MCP 客户端 + 大模型对话循环 + 技能）
│   ├── LitSSHmcp.App/            # WPF 管理界面（Views/ + ViewModels/）
│   └── LitSSHmcp.Cli/            # CLI
└── tests/
    ├── LitSSHmcp.Tests/           # Core xUnit 单元测试
    ├── LitSSHmcp.McpServer.Tests/ # MCP 服务器层一致性测试
    ├── LitSSHmcp.Agent.Tests/     # Agent 会话/技能/上下文测试
    └── LitSSHmcp.App.Tests/       # App 画布/路由引擎/几何测试
```

## 3. MCP 服务器层（LitSSHmcp.McpServer）

- `Program.cs` 用 `Host.CreateApplicationBuilder` 组装依赖注入，然后：
  - `.WithStdioServerTransport()` —— JSON-RPC 走 stdin/stdout；
  - `.WithTools<T>()` 注册 17 个工具类，共 53 个工具（详见 [TOOLS.md](TOOLS.md)）；每个工具带 `ReadOnly`/`Destructive`/`Idempotent`/`OpenWorld` 注解；
  - **工具分组**：启动时读取 `config.json` 的 `tools.enabledGroups`（`AppConfig.Tools` + `ToolGroups.ResolveEnabled`，留空=全部），按分组**条件注册** `WithTools<T>()`，可只暴露部分工具以降低 AI 上下文占用与误选；未知分组忽略并启动告警（见 [TOOLS.md](TOOLS.md)「工具分组」）；
  - **工具清单单一来源**：`mcp_usage_guide` 的工具清单由 `[McpServerTool]`/`[Description]` 反射生成（不手写）；`tests/LitSSHmcp.McpServer.Tests` 校验"已注册工具 ↔ `docs/TOOLS.md` ↔ 指南"一致；
  - **MCP 协议增强**：`initialize` 返回 **Server Instructions**（会话级行为约定，见 `Services/McpServerInstructions.cs`）；文件上传/下载通过注入的 `IProgress<ProgressNotificationValue>` 向前端推送 `notifications/progress`；
  - 日志通过 `LogToStandardErrorThreshold` 全部导向 **stderr**，同时经 `FileLoggerProvider` 写入 `%APPDATA%\LitSSH\logs\mcp-YYYYMMDD.log`（保留 7 天），stdout 严格保留给 MCP 协议（否则会污染协议流）。
- 启动时执行：配置加载（触发 `schemaVersion` 迁移）→ 拓扑库初始化 → 审计库初始化。
- DI 注册的关键服务：

| 服务 | 实现 | 说明 |
|------|------|------|
| `IConfigService` | `ConfigService` | 配置读写 + DPAPI 加密迁移 |
| `ISshService` | `SshService` | 命令执行/文件传输/连接测试（经 `ISshConnectionPool` 复用连接） |
| `IAuditLogService` | `AuditLogService` | SQLite 审计（命令 + SQL） |
| `IApprovalService` | `ApprovalService`（桌面通道 `DesktopApprovalService`） | 多通道审批分发（`security.approval.channels`：`desktop`/`cli`），并发等待、首个决定者生效、超时/弃权→拒绝（fail-closed）；`security.approval.mode`：`manual`(默认)/`auto-approve`(危险,全放行)/`auto-reject`(全拒绝) |
| `ICommandFilterService` | `CommandFilterService` | 命令黑名单/敏感规则（经 `ISecurityOptionsProvider` 按文件 mtime 热更新） |
| `ISqlFilterService` | `SqlFilterService` | SQL 只读/危险/敏感过滤（同上，热更新） |
| `ISecurityOptionsProvider` | `SecurityOptionsProvider` | 按 `config.json` 最后写入时间缓存安全配置，改规则无需重启 |
| `ISshKnownHostsStore` | `FileSshKnownHostsStore` | SSH 主机密钥指纹存储（TOFU） |
| `ITargetLimiter` | `TargetLimiter` | 按目标（服务器/数据源）限制并发与每分钟调用数 |
| `ISshConnectionPool` | `SshConnectionPool` | 每服务器复用最多 `maxPerServer` 条 SSH 连接、空闲自动断开（`connectionPool`，热读取） |
| `IMySqlConnectionProvider` | `MySqlConnectionProvider` | 建立直连或隧道连接，返回 `IDatasourceSession` |
| `IPostgresConnectionProvider` | `PostgresConnectionProvider` | 建立 PostgreSQL 直连或隧道连接，返回 `IPostgresSession` |
| `IRedisConnectionProvider` | `RedisConnectionProvider` | Redis 直连/隧道会话（RESP2 + AUTH/SELECT），返回 `IRedisSession` |
| `IDatasourceDriverRegistry` | `DatasourceDriverRegistry` | 按 `datasource.type` 分发驱动（当前登记 mysql、postgres、redis） |
| `ITopologyStore` / `ITopologyService` | `TopologyStore` / `TopologyService` | 拓扑边 SQLite 存储 / 拓扑图合并与依赖查询 |
| `ITopologyLayoutStore` | `TopologyLayoutStore` | 手动布局（节点位置/尺寸、端点锚点）JSON 持久化（`%APPDATA%\LitSSH\topology-layout.json`，与语义配置 `config.Relations` 分离） |

## 4. 核心服务（LitSSHmcp.Core）

### 4.1 SSH（`Services/SSH`）

- `SshClientFactory`：按 `SshServerConfig` 构造 SSH.NET `SshClient`/`SftpClient`（密码认证）。
- `SshService`：执行命令（含超时[`ExecuteCommandAsync` 支持 `timeoutSeconds`]、sudo 包装）、文件上传/下载（进度回调）、目录列举、连接测试。命令/文件传输经 `ISshConnectionPool` 获取连接：**命令与 SFTP 通道各自**每服务器复用最多 `connectionPool.maxPerServer` 条（执行完不断开、放回池；每条连接各自串行化，并发调用分配到不同连接），空闲 `connectionPool.idleTimeoutSeconds` 自动断开，`keepAliveSeconds` 防静默掉线，`connectTimeoutSeconds` 设连接超时；连接池禁用时退回"每次新建→执行→断开"。`ssh_test_connection` 仍用独立新连接做真实探测。批量下载 `DownloadBatchAsync` 复用**同一条 SFTP 连接**下载多文件/目录（一次审批）。
- 所有命令执行前先过 `ICommandFilterService`：`Blocked` 直接拒绝、`Sensitive` 交 `IApprovalService` 弹窗确认，结果无论成败写入审计。用户直传命令还会先过“防挂起”检查（`tail -f`/交互式/`sudo` 等直接返回 `blocking_command`）。

### 4.2 数据源（`Services/Datasource`）

- `IDatasourceDriver`：数据源驱动抽象（TestConnection/Query/Execute/Explain/Diagnostics），配套 `DatasourceTestResult` 等结果模型。
- `DatasourceDriverRegistry`：以 `IEnumerable<IDatasourceDriver>` 注入，按驱动自报的 `Type` 建索引；新增数据源类型只需实现驱动并注册到 DI（`Program.cs` / App 工厂），无需改动注册表构造函数。
- `MySqlDriver`：基于 MySqlConnector 的 MySQL 实现，只读校验/诊断采集在驱动内完成。
- `PostgresDriver`：基于 Npgsql 的 PostgreSQL 实现；诊断走 `pg_stat_activity` / `pg_stat_database` / `pg_stat_replication`（连接、活动会话、等待锁、缓存命中、死锁）。
- `RedisDriver`：自研极简 RESP2 客户端（`RedisProtocol.cs`，无第三方 Redis SDK）的 Redis 实现；Test/Diagnostics 走 INFO/DBSIZE/CLIENT LIST/SLOWLOG/CONFIG，Query 只允许只读白名单命令，Execute 先过 `RedisCommandPolicy` 硬拒绝再交审批。
- `MySqlConnectionProvider`：**统一连接入口**，按 `accessMode` 决定连接方式，返回会话对象：
  - `direct`：MCP 部署机可直达数据库 → 直接建连；
  - `sshTunnel`：数据库仅内网可达 → 经 `tunnelServerId` 指定的 SSH 服务器做**本地端口转发**（见下）。
- `PostgresConnectionProvider`：PostgreSQL 版统一入口，复用同一套隧道/限流/密钥校验链路；连接默认库（未配置时用 `postgres`）。
- `RedisConnectionProvider`：Redis 版统一入口，复用同一条隧道/限流/密钥校验链路（`TunnelServerResolver` 从 MySQL 侧抽出共用），连接后按需 `AUTH`（密码/ACL 用户名只发给 Redis，不落跳板机）与 `SELECT` 默认 DB。
- `SshTunnel`：SSH.NET `ForwardedPortLocal` 封装，`IDisposable`；隧道建立后连接指向 `127.0.0.1:随机端口`，**MySQL 密码只发给 MySQL，不落在跳板机**。若 `tunnelServerId` 为空，则按拓扑关系 `ssh:xx --canAccess--> ds:xx` 自动推导跳板。
- 凭据边界：`DataSourceConfig`/`SshServerConfig` 的密码由 `ConfigService` 在持久化时用 DPAPI 加密（`enc:` 前缀），仅 Windows 当前用户可解密；MCP 工具入参只接受 `datasourceId`，出参结构中**从不包含 password 字段**（host/port/username 暴露给 AI，用于与应用日志中的连接串比对）。

### 4.3 SQL 安全与审计（`Services/Security` + `Services/Storage`）

- `SqlFilterService`（`ISqlFilterService.cs`）：
  - `CheckReadOnly`：只允许 `SELECT/SHOW/EXPLAIN/DESC/WITH`，多语句/命中黑名单 → `Blocked`；
  - 写语句分类：`DROP DATABASE/TABLE`、无 WHERE 的 `DELETE/UPDATE`、`TRUNCATE`、`GRANT` 等 → `Blocked`；`INSERT/UPDATE/DELETE/DDL` → `Sensitive`（需桌面确认）。
- `RedisCommandPolicy`（`RedisCommandPolicy.cs`）：Redis 命令三档分类 —— 只读白名单（GET/INFO/SLOWLOG GET/CONFIG GET 等）→ `redis_read` 直接执行；危险与阻塞类（`SHUTDOWN`/`FLUSHALL`/`FLUSHDB`/`DEBUG`/`SWAPDB`/`REPLICAOF`/`SUBSCRIBE`/`BLPOP`/`MODULE LOAD` 等）→ 硬拒绝；其余写命令 → `redis_execute` **一律桌面审批**（第一期不分级）。
- `AuditLogService`：SQLite `%APPDATA%\LitSSH\audit.db`（WAL 模式 + `busy_timeout`，按时间/服务器/数据源建索引）：
  - `AuditLogs`：SSH 命令/操作审计（既是操作日志也是审计日志）：命令执行、审批拦截、只读探测、列表元数据、文件传输；每条带 `SessionId`（每次启动服务生成）、`Tool`、`Category`（`exec`/`gate`/`probe`/`meta`/`transfer`）与 `Decision`（Gate 类审批决策）；
  - `SqlAuditLogs`：SQL 操作（query/execute/explain，含 blocked/rejected）；Redis 命令审计也写入本表（`operation` 同 query/execute/diagnostics，SQL 列记录命令原文）；同样带 `SessionId`/`Tool`/`Category`/`Decision`；
  - `Sessions`：MCP 会话表（会话ID → 客户端名称/版本、首末活动），由 `McpSessionFilter` 从 `initialize` 后的客户端信息回填；
  - `TopologyEdges` / `TopologyNodes`：`topology_discover` 自动发现的关系缓存与节点信息（端口/类型/路径）。
- **审计防篡改与永久归档**：`AuditChain`（HMAC-SHA256 哈希链，密钥 DPAPI 保护于 `audit.db.key`；无密钥时退化为 SHA-256）+ 历史归档表 `AuditLogsHistory`/`SqlAuditLogsHistory`——超过 `security.audit.retentionDays` 的活动记录**移动**到历史表（永久保留，链不删除、Id 不变）；`IAuditLogService.VerifyChainAsync()` 校验整链完整性，App「审计日志」提供“校验完整性/含归档”。`Category`/`Decision` 一并纳入哈希链（审计格式版本 v5；版本落后时启动重建）。
- **带外审批**：`ApprovalService` 按 `security.approval.channels` 启用 `desktop`（`DesktopApprovalService` 弹窗）与 `cli`（写 `%APPDATA%\LitSSH\approvals\pending-<id>.json` 等待决策文件，操作员用 `litssh approve/deny <id>` 决定）；共享 `ApprovalFileStore` 文件格式。审批前先看 `security.approval.mode`：`auto-approve`/`auto-reject` 直接放行/拒绝（不弹窗、不等 CLI）。
- `PathPolicy`：文件传输路径白名单校验（本地 `GetFullPath` 规范化 + 前缀匹配；远程 POSIX 规范化并拒绝 `..` 穿越）。

### 4.4 拓扑（`Services/Topology` + `Models/TopologyModels.cs`）

- 资产节点统一格式（`AssetNode` 辅助类）：`ssh:服务器ID` / `ds:数据源ID` / `app:应用ID`。
- 关系类型（`RelationConfig`）：`runsOn`（应用**或数据库**运行在服务器）、`connectsTo`（应用连接数据库）、`canAccess`（服务器可访问数据库，同时充当隧道跳板推导依据）；另有通用占位 `relatedTo`（无特定语义，仅作待确认关联，仍按方向参与依赖查询；`type` 留空时默认归为 `relatedTo`）。
- **双轨来源**：
  1. 人工声明：`config.json` 的 `relations` 数组；
  2. 自动发现：`TopologyService.DiscoverAsync`（**节流**：同时只跑一个）扫描服务器上的 java 进程 / 通用服务进程（nginx/mysql/redis 等，按进程名精准匹配，带监听端口）/ ESTAB 网络连接 / 配置文件 JDBC(`mysql`/`postgresql`)/Redis/RabbitMQ/Kafka/Nginx(`upstream`/`proxy_pass`) / `docker ps` 容器，并用 MySQL `SHOW PROCESSLIST` 反查客户端 IP，写入 `TopologyEdges`（+ 节点信息 `TopologyNodes`）；匹配到已登记资产则连真实节点，未匹配的以 `*:disc:*` "待确认"节点出现，可在拓扑页右键确认（登记为资产）/删除；本地客户端(localhost)归属到所属服务器。
  3. 关系校验与清理：`RelationRules.TryValidate` 保证关系类型与节点类型匹配（拒绝 `ssh --runsOn--> ssh` 等）；删除服务器/数据源/应用时级联删除手动关系并调用 `ITopologyStore.RemoveEdgesByNodeAsync` 清理自动发现边。
- `TopologyService` 合并两轨生成 `TopologyGraph`，提供 `GetDependencies(assetId)` 上下游查询。

### 4.5 配置与加密（`Services/Storage` + `Services/Security`）

- `ConfigService`：读写 `%APPDATA%\LitSSH\config.json`；**读写双向迁移**——发现明文密码即加密为 `enc:` 形式，保证磁盘无明文。
- `ConfigMigrator`：按 `AppConfig.schemaVersion` 迁移旧结构（当前版本 1）；缺失版本号的旧文件自动补写。
- `ConfigPaths` / `AppConfigJson`：统一 `%APPDATA%\LitSSH` 下的路径（`config.json`/`audit.db`/`snapshots.db`/`known_hosts.json`/`logs`）与 JSON 序列化选项。
- `DpapiSecretProtector`（`ISecretProtector.cs`）：`ProtectedData`，`LocalMachine`/`CurrentUser` 范围，前缀 `enc:` 标识。
- 配置结构：`schemaVersion`、`servers[]`、`dataSources[]`、`applications[]`、`relations[]`、`security{commandFilter, sqlFilter, fileTransfer}`（完整示例见 README 快速开始）。

### 4.6 服务器快照（`Services/Snapshot`）

- **工具**：`ssh_snapshot_get`（读本地最新/历史快照）、`ssh_snapshot_refresh`（同步采集；同机单飞 + 库内 `Running` 唯一部分索引跨进程互斥；失败也落库并保留部分数据）。归 `ssh` 分组。**默认返回各维度概览**（`status`/`dataChars`/`headline` 关键指标 + `hint`）而非全量 `data`，用 `section=` 取单维度完整数据、`detail="full"` 取全部；`SnapshotTools.BuildDataView` 负责视图与硬上限压缩（截断超长字符串/数组），避免一次性把 >15 万字符塞进模型上下文。**刷新节流**：`snapshot.minRefreshIntervalSeconds`（默认 60s）内未 `force` 的 `ssh_snapshot_refresh` 直接返回已有快照（`status=fresh`）。
- **存储**：独立 SQLite `%APPDATA%\LitSSH\snapshots.db`（`Snapshots` 记录 + `SnapshotEvents` 事件流水；格式版本 v4，版本不一致直接重建）。`SnapshotStore` **惰性建表**（各进程安全），启动 `InitializeAsync` 重置遗留 `running` 孤儿。
- **可插拔采集维度**（`ISnapshotCollector`，DI 注册、按 `Order` 顺序执行）：`resource`（资源态势）、`portmap`（端口↔进程↔用户↔服务三元组 + 程序路径 `exe`/完整启动命令 `cmdline`；无标记解析，兼容 su 交互式 PTY 回显）、`docker`（容器与资源）、`nginx_tls`（用"运行中 nginx 的可执行路径"执行 `-T` 取全量有效配置 + 域名/证书）、`systemd`（单元健康）、`security`（安全巡检：SSH/防火墙/fail2ban/MySQL 账户/高危端口；`mysql.user` 优先用"匹配该服务器的已配置数据源凭据"核查）。
- **提权**：`snapshot.useSudo`（默认 `true`）+ 服务器 `SudoType` 决定是否以 sudo/su 执行**内置固定只读命令**；未配置/关闭则降级（section `degraded`），命令仍受命令过滤器约束。
- **扩展**：新增维度 = 实现 `ISnapshotCollector` 并在 `Program.cs` 注册；`snapshot.retentionPerServer` 控制每机保留份数（级联清理事件）。

### 4.7 AI 运维助手（`LitSSHmcp.Agent`）

- **定位**：App 内置的大模型运维智能体，让用户"配置模型即可用自然语言运维"，无需依赖第三方 AI 客户端配置 MCP。
- **技术栈**：`Microsoft.Extensions.AI`（`IChatClient` 抽象）+ 官方 `ModelContextProtocol`（**客户端**）+ OpenAI 兼容端点连接器（`Microsoft.Extensions.AI.OpenAI`，覆盖 DeepSeek/Qwen/Kimi/GLM/硅基流动/Ollama）。
- **通道**：`McpToolHost` 以 **stdio 子进程**启动本机 `LitSSHmcp.McpServer`（发布包内置 `mcp/LitSSHmcp.McpServer.exe`，`AgentPaths` 自动探测/可配置），`McpClient.ListToolsAsync()` 得到工具（`McpClientTool : AIFunction`）。**所有操作经 MCP 工具**，故审批/过滤器/审计哈希链/工具分组开关原样生效。
- **对话循环**：`AgentSession` 手动驱动"模型→工具调用→结果回灌"循环（`FunctionCallContent`/`FunctionResultContent`），可约束最大轮数、裁剪上下文、推送工具轨迹事件。
- **技能**：`SkillRegistry` 加载 markdown 技能（默认内置 `litssh-mcp-ops-skill`，可配置目录；技能文档保持通用）——**默认只注入顶层 `SKILL.md`**，`references/` 与 `*.template.md` 不默认注入，改由本地工具 **`skill_list` / `skill_read`**（`SkillTools`，限定技能目录内）按需读取，降低每轮系统提示体积；`SystemPromptBuilder` 拼装"基础约定 + MCP `server instructions` + 技能 + 技能参考文件清单 + 用户附加提示 + 本地工具(工作区文档/计划)说明"注入系统提示。内置**工作区文档工具** `ops_doc_read/write/append/patch/list`（`WorkspaceTools`）与**计划工具** `update_plan`（`PlanTools`，单代理内规划器），均为本地非 MCP。发送模型前可用 `ToolDescriptions.Compact` 精简 MCP 工具描述（`agent.compactToolDescriptions`，默认开）。
- **大结果落盘**：`SpillStore` 把超过 `toolResultMaxChars` 的工具结果写入 `spillDir`（默认 `%APPDATA%\LitSSH\spills`，按 `spillRetentionDays` 清理），上下文只保留预览 + `spill://<handle>`；`SpillTools` 提供 `spill_list`/`spill_read`(分页)/`spill_grep`。`AgentSession.BuildToolResultContextText` 负责落盘或退回截断。
- **子代理隔离**：`SubAgents`（`run_subagent`）把独立只读取证任务交给一个**隔离的 `AgentSession`**（仅 MCP/技能/落盘只读工具，无工作区写与计划，防副作用与递归），只把结论摘要作为工具结果返回主上下文；受 `agent.enableSubAgent` 控制。
- **Prompt Caching 指引**：系统提示（含技能）与工具定义为**每轮不变的稳定前缀**，可变内容（用户消息/工具结果/摘要）在后，利于 OpenAI/DeepSeek 等 **provider 端前缀缓存**（前缀 ≥1024 tokens 才生效）；保持系统提示、技能、工具分组稳定即可持续命中；修改后首轮会重建缓存。
- **输出风格**：`SystemPromptBuilder.AppendResponseStyle` 按 `agent.responseStyle`（concise/standard/detailed）注入"结论先行、只讲重点、控制篇幅"的运维风格要求，配合各模型 `maxTokens` 控制输出 token。
- **长期记忆 / RAG**：`EmbeddingClientFactory`（OpenAI 兼容 embeddings）+ `AgentMemoryStore`（`agent.db` 的 `agent_memory` 表存向量）+ `AgentMemoryService`（索引/召回）；`AgentRuntime` 索引工作区文档、每轮对话落库，`AgentSession` 在发送前**召回相关记忆**注入上下文。`ToolFilter` 按 `allowedToolGroups` + 只读模式裁剪工具。
- **健壮性**：`ResilientChatClient`（`DelegatingChatClient`）为每模型提供**并发上限 + 单次超时 + 瞬时错误重试**（流式仅在首包前重试）；`ChatErrorClassifier` 把异常分类为 `auth/rate_limit/timeout/network/server/bad_request` 供 UI 提示；`ContextStore.PruneAsync` 按保留策略裁剪会话/消息；系统提示内置提示注入防护。
- **上下文管理**：`AgentSession.ManageContextAsync` 每轮发送前按 **user 轮边界**丢弃最旧整轮（保证 `tool_calls↔tool` 配对），并同时受条数与 token 双阈值约束；被裁旧轮经 `SummarizeAsync` 压缩为**滚动摘要**（`autoSummarize`）后作为一条 System 消息注入。`ReplaceHistory`（编辑/重发）会清空摘要。工具结果注入上下文前按 `toolResultMaxChars` 截断；`CompactIfNeededAsync` 在每步工具后复查，超限时 `CompactToolResults` 将本轮较早的工具结果替换为占位符（保持 `CallId` 配对）。
- **上下文**：独立 SQLite `%APPDATA%\LitSSH\agent.db`（`agent_sessions`/`agent_messages`/`agent_memory`）。
- **配置**：`AppConfig.Agent`（多 provider、active、systemPrompt、skillsDir、workspaceDir、contextLimit、readOnly、allowedToolGroups、memory、mcpServerPath、maxToolIterations）；API Key 纳入 DPAPI `enc:` 加密。
- **UI**：`Views/AgentWindow`（按"用户指令(时间)/工具过程/回答(时间与耗时)"的轮次展示 + `Controls/MarkdownBox` Markdown 渲染 + 工具过程可折叠 + 任务计划面板）+ `Views/AgentSettingsWindow`（模型/分组/记忆/参数），主菜单「AI 运维助手」打开（非模态独立窗口）。

## 5. WPF 管理界面（LitSSHmcp.App）

- MVVM：`Views/`（窗口）+ `ViewModels/`（`INotifyPropertyChanged` + `RelayCommand`）。
- 窗口清单：
  - `MainWindow` / `MainViewModel`：主界面壳层。左侧图标导航栏 + 右侧**主区域页面宿主**（按 `MainPage` 切换）：**主页**（服务器列表 + 会话标签）、**SSH 服务器管理**（`ServerManageView`）、**数据源管理**（`DatasourceManageView`）、**应用管理**（`ApplicationManageView`）。资产拓扑/审计日志仍为独立窗口；
  - `ServerEditWindow` / `ServerEditViewModel`：服务器新增/编辑；
  - `DatasourceManageView` / `DatasourceManageViewModel`：数据源增删改与连通性测试（主区域页面，不再弹窗）；
  - （原「拓扑关系管理」窗口已并入下方「资产拓扑」可视化编辑器，不再单独提供）
  - `ApplicationManageView` / `ApplicationManageViewModel`：应用(`app:`)节点维护（含 Docker 容器名 `ContainerName`；主区域页面，不再弹窗）；
  - `AuditWindow` / `AuditViewModel`：命令/SQL 审计查看、CSV 导出、**含归档**（永久保留的历史表）、**校验完整性**（哈希链）；**非模态独立窗口**（不置顶，可与主界面同时操作）；
  - `AppSettingsWindow`（底部 **设置** 入口，多 Tab）：`AppearanceSettingsView` / `AppearanceSettingsViewModel`（全局 **界面主题/强调色**，写入 `ui.theme`/`ui.accent`）；`SecuritySettingsView` / `SecuritySettingsViewModel`（**MCP 全局开关**、命令/SQL 过滤、文件传输、主机密钥、发现路径、限流、审计策略、**审批通道**、**审批模式**、**查询结果脱敏**）；`ToolGroupsView` / `ToolGroupsViewModel`（勾选 `tools.enabledGroups`）；`ConfigTransferView` / `ConfigTransferViewModel`（配置导入/导出）；`McpToolsView`（**MCP 工具说明**）；
  - `TopologyWindow` / `TopologyViewModel`：资产拓扑可视化 + 自动发现入口；**非模态独立窗口**（不置顶，可与主界面同时操作）。`runsOn` 的应用**与数据库**都内嵌在所属服务器区块内（一眼看出服务器上运行了哪些服务/库），节点标题附带端口（如 `订单库 :3306`），`connectsTo`/`canAccess` 以带类型标注的连线绘制，数据库运行在所连服务器上时省略冗余 `canAccess`，自动发现的关系用虚线区分；鼠标悬浮任一节点显示详情（主机/端口/账号/类型/描述/标签，密码等敏感信息不展示）。连线绘制在**服务器区块之上、叶子节点之下**，采用**避障正交路由**（Hanan 栅格 + A*，不直穿其它节点；端点从四边中点择优；源/目标所在服务器区块对其子节点透明，可进入 `runsOn` 嵌套区块；回退 Z 形），拐角圆角化，起点圆点、终点箭头；交叉处过桥。**可交互编辑**：拖动/缩放节点（拖服务器带动子节点；位置按「起点 + 总位移」绝对推导、往返不漂移；**四角手柄 + 四边内侧直接拉伸**，最小尺寸 80×36）、选中节点从边中点端口**拖拽建边**（自动推断类型并做逻辑校验，含 `runsOn` 每节点唯一）、**点选连线删除**（手动关系删 `config.Relations`；自动发现边清理 `TopologyEdges`）、拖动连线**端点锚点**指定接边位置、**网格吸附**、**Ctrl+Z/Y 撤销重做**、「重新自动布局」；手动布局存 `%APPDATA%\LitSSH\topology-layout.json`。画布支持**无限平移/缩放**（滚轮缩放、适应窗口、`Ctrl+0` 重置；右上角**「更多操作 ▾」**下拉含 ＋/－/适应/100%/刷新/**导出图片…**）；关系属性面板可拖动；拖动节点进出服务器时按**几何落点**自动增删 `runsOn`（完全落入=建立、拖出=弹窗确认删除、部分重叠=禁止回退），落点判定见 `TopologyViewModel.EndNodeDragAsync`。`runsOn` 托管的子节点用**点线边框**渲染；画布有**网格背景**且移动/缩放吸附 10px；调整大小与其它节点接触时就地停住、被挡后以当前几何**重锚**（反向拖动无死区），服务器缩小时托管子节点自动收紧、**不参与** `ServerOverlapInvalid` 校验；手动锚点通过 `FixedFromSide/ToSide` 固定侧向、移动/拖动过程（快路由）也不漂移；拖动过程跳过过桥计算并跳过吸附后未位移的重算以降低卡顿。画布网格用独立 `GridLayer`（`DrawingBrush.Transform` 跟随缩放/平移，任何缩放/平移后边缘都有网格）；空白处**左键按住即可平移**（不再用中/右键）；指针悬停节点四边/四角切换缩放光标（**四边按下即可拉伸该侧**）、节点上切换移动光标；节点**右键菜单**打开 `NodeRelationsWindow` 列出该节点相关关系并可编辑（`RelationRules` 校验）/删除（列表用节点名、当前节点红色加粗、悬浮显示 ID）。解除 `runsOn` 后由 `RelocateOrphanedStandalone` 将残留的独立节点移到就近空白处；同走廊连线不再分道错位（`LaneGap=0`，连接点可重叠）。
  - `McpToolsView`（设置窗口 Tab「MCP 工具说明」）：展示 MCP 接入配置 + 意图路由表 + 全部工具的参数/用法。内容来自 `docs/TOOLS.md`（以 `EmbeddedResource` 嵌入 `LitSSHmcp.App.csproj`），因此**与 MCP 服务器端工具注解共享唯一事实来源**：工具变动时须同步 ① `src/LitSSHmcp.McpServer/Tools/*.cs` 的 `[McpServerTool]`/`[Description]` 注解（`mcp_usage_guide` 清单由其反射生成，无需手改）② `docs/TOOLS.md` ③ `Program.cs` 的 `WithTools<T>()`；三者一致性由 `tests/LitSSHmcp.McpServer.Tests` 守门。左侧按 **`概览与接入` + 14 个工具分组**展示，分组标题为 `## <中文名>（<分组键>）`，分组键与「工具分组设置」的 `tools.enabledGroups`（`ToolGroups.All`）保持一致；`### \`工具名\`` 计为工具，其它 `###` 子标题并入章节内容。
- 主界面 `MainWindow` 采用**左侧图标导航栏 + 右侧主区域页面宿主**：顶部导航为 主页 / SSH 服务器管理 / 数据源管理 / 应用管理（高亮当前页）；分割线下为 资产拓扑 / 审计日志（独立窗口）；**最下方为 设置**（外观 / 安全设置 / 工具分组 / 导入导出 / MCP 工具说明 多 Tab 窗口）。SSH 服务器管理页顶部仅 添加/刷新，其余操作（连接/编辑/采集快照/快照历史/删除）走条目右键菜单，双击行打开编辑窗口。**主页为看板**：快捷入口（打开会话管理/数据源/应用/拓扑/审计/AI 助手）、数据看板（服务器总数·禁用数、数据源数、应用数）、AI 快捷提问（输入问题或附件后发送，填入助手输入区）。**「连接」打开独立的 `SshSessionWindow`（SSH 会话管理，多标签、非模态/不置顶，每服务器一个标签）。**
- 配置导入/导出：整合进底部 **设置 → 导入/导出** Tab（导出可选脱敏，不含任何密码；导入覆盖当前配置）；同窗口 **工具分组** Tab 用于裁剪暴露给 AI 的工具分组。

### 线程模型（重要约定）

WPF 只有 UI 线程（Dispatcher）可访问界面元素，**严禁在 UI 线程上同步等待异步操作**（sync-over-async），否则会造成死锁：

```
UI线程: LoadConfigAsync().GetAwaiter().GetResult()   ← 阻塞
                │
LoadConfigAsync 内部 await 的续延 ──投递回 Dispatcher──> 永远排不上队 → 死锁
```

- 2026-09-30 曾因此导致"数据源管理 → 添加"按钮点击后整个界面卡死，修复方式：`DatasourceEditViewModel` 构造函数改为接收服务器列表参数，由调用方 `Add()/Edit()` 先 `await LoadConfigAsync()` 再构造窗口（见 `DatasourceManageViewModel.cs`）。
- **约定**：ViewModel 中与 I/O 相关的入口（构造后的初始化、命令回调）一律用 `async` 方法 `await`，不使用 `.Result`/`.GetAwaiter().GetResult()`。控制台程序（`McpServer/Program.cs` 启动期）没有 Dispatcher 上下文，启动期的同步等待不适用此限制，但也不要扩大使用范围。

## 6. CLI（LitSSHmcp.Cli）

单文件 `Program.cs`：`litssh list` / `litssh connect <server>`（交互式会话）/ `litssh run <server> <cmd>`，复用 Core 的 `ConfigService` 与 `SshService`。

## 7. 关键数据流

### 7.1 AI 执行一条命令

```
AI客户端 ──stdio──> ssh_execute_command
  → CommandFilterService 过滤 (Blocked? 直接返回 / Sensitive? ApprovalService 审批: desktop 弹窗或 cli 带外)
  → SshService 执行 (SSH.NET)
  → AuditLogService 写 CommandAuditLogs
  → JSON 返回 {success, stdout, stderr, exitCode, durationMs}
```

### 7.2 AI 查询 MySQL（隧道模式）

```
AI客户端 ──> mysql_query(datasourceId, sql)
  → SqlFilterService.CheckReadOnly (非只读 → Blocked + 审计)
  → MySqlConnectionProvider.GetSessionAsync:
       accessMode=sshTunnel → SshTunnel 建立本地端口转发 → 连 127.0.0.1:随机端口
       accessMode=direct    → 直连 host:port
  → MySqlDriver.QueryAsync → 截断到 maxRows
  → AuditLogService 写 SqlAuditLogs
  → JSON 返回 {columns, rows, rowCount, truncated, durationMs}
```

### 7.3 AI 执行 Redis 命令（隧道模式）

```
AI客户端 ──> redis_read(datasourceId, command)
  → RedisCommandParser.Split 解析(支持引号/转义)
  → RedisCommandPolicy.Classify: Blocked→拒绝 + 审计; Write→提示改用 redis_execute
  → RedisConnectionProvider.OpenAsync:
       accessMode=sshTunnel → SshTunnel 本地转发 → 连 127.0.0.1:随机端口
       连接后 AUTH(密码/ACL用户名只发给Redis) + SELECT 默认DB
  → RedisClient 执行 RESP2 命令 → RedisValueFormatter JSON化+截断
  → AuditLogService 写 SqlAuditLogs(SQL列记录命令原文)
  → JSON 返回 {result, truncated, durationMs, accessMode, viaTunnelServer}

AI客户端 ──> redis_execute(datasourceId, command)
  → 同上解析与分类: Blocked→拒绝; ReadOnly→提示改用 redis_read
  → IApprovalService 弹桌面确认(第一期写操作一律需确认) → 执行 → 审计
```

### 7.4 全链路故障排查（AI 的典型路径）

```
topology_get_overview (全局拓扑) → topology_get_dependencies(app:xx) (定位依赖)
  → SSH侧: ssh_execute_command 看进程/端口/应用日志
  → 数据库侧: mysql_diagnostics → mysql_query(processlist/慢日志) → mysql_explain(问题SQL)
  → 缓存侧: redis_diagnostics(内存/命中率/慢日志) → redis_read(具体key)
  → 跨机关联: 应用日志中的数据库IP ↔ datasource_list 的 host/port
  → 拓扑缺失时 topology_discover 补全
```

## 8. 安全模型汇总

| 层 | 机制 | 实现位置 |
|----|------|---------|
| 全局开关 | `security.enabled=false` 时拒绝所有工具调用（call-tool 请求中间件，热生效） | `McpGlobalSwitch` + `McpServerOptions.Filters` |
| 命令执行 | 黑名单直接拒绝 / 敏感命令确认 | `CommandFilterService` + `ApprovalService` |
| 防挂起 | `ssh_execute_command` 先拦“会挂起/需交互”的命令（`tail -f`/`docker logs -f`/`journalctl -f`/`vi`/`top`/`sudo`/`ping`无`-c` 等）返回 `blocking_command` 并给替代写法；`timeoutSeconds` 可调(默认60) | `ToolSupport.BlockingCommandHint` + `CommandTools`/`SudoTools` |
| 授权确认 | 多通道（`security.approval.channels`，默认 `["desktop","cli"]`）任一通道给出结论即生效；超时返回 `approval_timeout`、全体弃权返回 `approval_unavailable`、任一通道拒绝即 `rejected`（fail-closed）；desktop 通道支持 `style=process`（独立子进程弹窗，规避宿主隐藏窗口，原生带超时的消息框）/`dialog`/`native`，cli 通道写待决文件由 `litssh approve/deny` 决定 | `ApprovalService` + `ApprovalDialog` / `ApprovalRequestHost` / `CliApprovalChannel` |
| 文件传输 | 开关 + 本地/远程路径白名单（`..` 穿越拦截）+ 大小上限 + 人工确认 | `FileTransferTools` + `PathPolicy` + `SshService` |
| SQL | 只读工具只放行真正只读的语句（数据修改型 CTE、`EXPLAIN ANALYZE <DML>` 会被判为写操作）；写工具拦截危险语句、敏感语句用户确认 | `SqlFilterService` |
| Redis | 只读白名单放行；危险/阻塞命令硬拒绝；其余写命令一律用户确认（第一期） | `RedisCommandPolicy` + `RedisTools` |
| 凭据 | DPAPI 加密落盘（`enc:`）；工具入参只用 `datasourceId`；出参永不含密码 | `ConfigService` + `DpapiSecretProtector` |
| 隧道 | MySQL/PostgreSQL/Redis 密码只发给对应数据库，不经过跳板机配置 | `SshTunnel` |
| 主机密钥 | TOFU 校验 SSH 指纹，变化即拒绝（`security.sshHostKey.mode`） | `SshClientFactory` + `FileSshKnownHostsStore` |
| 限流 | 按目标限制并发/每分钟调用（`security.limits`） | `TargetLimiter` |
| 拓扑发现 | 扫描路径受白名单约束（`security.discovery.allowedSearchPaths`） | `TopologyTools` + `PathPolicy` |
| 审计 | 命令、SQL（含被拒绝/被驳回）全部落 SQLite（WAL + 索引）；原文开关/字面量脱敏/超期清理见 `security.audit` | `AuditLogService` + `SqlRedactor` |
| 配置热更新 | commandFilter/sqlFilter/fileTransfer 改动按文件 mtime 即时生效，无需重启 | `SecurityOptionsProvider` |
| 日志 | MCP 日志走 stderr（不污染 stdout 协议流）+ 本机文件 | `Program.cs` + `FileLoggerProvider` |
| 工具风险提示 | 53 个工具标注 `ReadOnly`/`Destructive`/`Idempotent`/`OpenWorld` | `[McpServerTool(...)]` |

## 9. 扩展点

### 9.1 新增数据源类型

1. 实现 `IDatasourceDriver`（`Services/Datasource/IDatasourceDriver.cs`）；
2. 实现对应的连接提供者（直连/隧道），并在 `Program.cs` 注册 `IDatasourceDriver` 实现（App 端在 `AppServiceFactory` 同步）；
3. 新增专用 MCP 工具类并在 `Program.cs` 用 `.WithTools<T>()` 注册（按 `datasource.type` 校验入参数据源类型）；
4. 同步 `docs/TOOLS.md`（唯一事实来源）与 App 内嵌的 MCP工具说明。
   参考已完成的 Redis：`RedisDriver` + `RedisConnectionProvider` + `Tools/RedisTools`（redis_read/redis_execute/redis_diagnostics）。

### 9.2 新增 MCP 工具

1. 在 `McpServer/Tools/` 的某个工具类（或新类）上加 `[McpServerTool(ReadOnly = true/false, Destructive = ...)]` + `[Description]`；
2. 新工具类需在 `Program.cs` 用 `.WithTools<T>()` 注册；
3. 工具说明采用"当用户问…时使用"的意图句式，并给相邻工具加反向提示（用户意图 → 工具的路由质量直接影响 AI 选工具的准确率）；
4. 涉及数据源的入参，`datasourceId` 描述统一写"可用 datasource_list 列出"；
5. 为工具补 `ReadOnly`/`Destructive`/`Idempotent`/`OpenWorld` 注解，便于客户端提示风险。

## 10. 开发与调试

### 构建与发布

```bash
dotnet build LitSSHmcp.slnx                      # 全量构建（应 0 警告 0 错误）
dotnet test LitSSHmcp.slnx                      # 全部测试(Core + McpServer + App，184 用例)
dotnet publish src/LitSSHmcp.McpServer -c Release -r win-x64 --self-contained -o publish
dotnet publish src/LitSSHmcp.Cli -c Release -r win-x64 --self-contained -o publish
```

### 拓扑画布性能诊断

设置环境变量 `LITSSH_PERF=1` 后启动 App，拓扑画布会把 `CommitLayout` / `RebuildEdges(fast|full)` 耗时、拖动会话汇总（`DragSession`，VM 每帧耗时）、`Zoom`/`Load` Scope 与帧率探针（`FrameProbe`，拖动/缩放期间挂 `CompositionTarget.Rendering`，含 `tier=0` 软件渲染/RDP 标记）写入 `%APPDATA%\LitSSH\topology-perf.log`——`DragSession` 与 `FrameProbe` 对照即可区分瓶颈在 VM 还是渲染层。默认关闭、零开销（代码内 `PerfLog.Enabled` 判断）。

### MCP stdio 冒烟测试要点

直接调试 stdio 服务器时有三个坑，必须注意：

1. **保持 stdin 打开**：客户端 stdin 一关，SDK 会提前退出且 `BufferedStream(Console.OpenStandardOutput())` 的响应不会刷出，表现为"请求无响应"；
2. **stdout 是协议流**：`initialize` → `notifications/initialized` → `tools/list` / `tools/call` 按 JSON-RPC 换行分帧；日志只应出现在 stderr；
3. **中文请求体要以 UTF-8 字节写入 stdin**：PowerShell 字符串直接写管道可能按本地代码页编码，导致服务端解析失败。建议向 `$p.StandardInput.BaseStream.Write(UTF8 bytes)`。

验收基线：`initialize` 成功、`tools/list` 返回 53 个工具、`mcp_usage_guide`/`datasource_list` 响应正常、`mysql_query`/`redis_read` 等错误路径返回结构化 `error`。上述 stdio 冒烟现已固化为 `tests/LitSSHmcp.McpServer.Tests` 的集成测试（真实启动服务器进程，用 `LITSSH_DATA_DIR` 指向临时目录隔离）。

### Windows 编码注意

- **不要用 PowerShell 的 `Set-Content -Raw`/`-replace` 批量改写源码文件**：PS 5.1 默认按本地代码页读写，会把 UTF-8 无 BOM 源文件写坏（曾导致 CS1513）。修改源码请用编辑器工具逐处编辑。
- 验证含中文的输出时，先落盘为 UTF-8 文件再用文本工具检查，避免控制台显示乱码造成的误判。
