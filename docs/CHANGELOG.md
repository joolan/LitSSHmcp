# Changelog

本项目的所有重要变更都记录在此文件。格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [未发布]

尚未打版本标签（无 release/tag）。

### 新增

#### 工具分组开关（按部署裁剪，可选）

- 新增 `config.json` 的 `tools.enabledGroups`（`AppConfig.Tools` + `ToolGroups.ResolveEnabled`）：只暴露部分工具分组，降低 AI 上下文占用与误选。留空/不写 = 全部启用，`"all"` = 全部，大小写不敏感，未知分组忽略并启动告警。
- 分组与工具类映射（`Program.cs` 按分组条件注册 `WithTools<T>()`）：`ssh`、`command`、`fileTransfer`、`datasource`、`mysql`、`redis`、`topology`、`guide`。
- 若仅启用 `mysql`/`redis` 而未启用 `datasource`，启动日志会告警（缺少 `list_datasources` 获取 `datasourceId`）。
- 桌面 App 菜单 **配置 → 工具分组设置**（`ToolGroupsWindow`）可视化勾选分组（启用全部/全部停用/重新加载/保存）；`["none"]` = 全部停用。
- 单元测试新增 `ToolGroupsTests`（含配置往返），用例总数 118 → 127。

#### Redis 数据源支持（第一期：只读 + 写操作一律审批，工具总数 22 → 25）

- **自研 RESP2 协议客户端**：`RedisProtocol.cs`（`RespCodec` 编码命令 / `RespReader` 解析应答 / `RedisClient` 建连，TCP + BufferedStream），**不引入第三方 Redis SDK**；UTF-8 解码失败的 bulk 转 base64 返回。连接支持 `direct` 与 `sshTunnel`（复用 MySQL 的隧道/限流/主机密钥链路，跳板推导抽出共用 `TunnelServerResolver`），连上后按需 `AUTH`（密码/ACL 用户名只发给 Redis，不落跳板机）与 `SELECT` 默认 DB。
- **驱动接入**：`RedisDriver : IDatasourceDriver`（Test / Query / Execute / Diagnose，Explain 明确提示"Redis 没有 EXPLAIN"）；`DatasourceDriverRegistry` 登记 `mysql` + `redis`（构造函数改为同时注入 `IMySqlConnectionProvider` 与 `IRedisConnectionProvider`）。
- **命令安全策略** `RedisCommandPolicy`（第一期固定内置，暂不可配置）三档：
  - 只读白名单（`GET`/`INFO`/`SLOWLOG GET`/`CONFIG GET`/`SCAN` 等）→ `redis_read` 直接执行；
  - 危险与会让连接挂起的命令（`SHUTDOWN`/`FLUSHALL`/`FLUSHDB`/`DEBUG`/`SWAPDB`/`REPLICAOF`/`SUBSCRIBE`/`BLPOP`/`CONFIG REWRITE`/`MODULE LOAD` 等）→ **硬拒绝，不会以任何方式执行**；
  - 其余写命令（`SET`/`DEL`/`EXPIRE`/`CONFIG SET`/`CLIENT KILL` 等）→ `redis_execute` **一律弹桌面审批**（第一期不做敏感度分级），执行与否全部写审计。
- **3 个新 MCP 工具**（22 → 25）：`redis_read`（只读，`maxItems` 截断数组）、`redis_execute`（写/管理，审批）、`redis_diagnostics`（`INFO`/`DBSIZE`/`CLIENT LIST`/`SLOWLOG`/`CONFIG` 关键项 + keyspace 摘要）。命令解析 `RedisCommandParser` 支持引号与反斜杠转义，结果经 `RedisValueFormatter` JSON 化（超长字符串/超大数组截断），失败返回结构化 `status`（`blocked`/`not_readonly`/`readonly_statement`/`rejected`/`redis_error`/`connection_error`/`invalid_command` 等）。
- **配置与界面**：数据源类型项"Redis（预留）"改为正式"Redis"，**切换类型时自动带出默认端口** 3306/6379；提示文案补充 ACL 用户名与 DB 索引语义；审计窗口 SQL 列头改为"SQL/Redis命令"（Redis 命令审计写入同一 `SqlAuditLogs` 表，SQL 列记录命令原文）。
- **文档与同步**：`docs/TOOLS.md`（唯一事实来源）新增第 6 组 Redis、路由表 4 行、错误约定；README 工具表/计数/安全控制/排查链路、`ARCHITECTURE`（接口表、核心服务、时序、安全模型、扩展点）、`UsageGuideTools` 内置清单（并补上此前遗漏的 `health_check`）同步更新。
- **单元测试**：新增 `RedisProtocolTests`（RESP 编码/解析/截断/异常）、`RedisValueFormatterTests`、`RedisCommandParserTests`、`RedisCommandPolicyTests`、`RedisEndToEndTests`（内存假 RESP 服务器：验证 AUTH/SELECT 握手、只读查询、写/危险命令拦截、诊断采集），用例总数 52 → **118**。

#### MySQL 数据源与资产拓扑（第二迭代核心功能）

- **数据源模型**：`DataSourceConfig` 支持配置任意数量的 MySQL（`host/port/username/password/defaultDatabase`），两种访问模式：
  - `direct` 直连：MCP 部署机可直接访问数据库；
  - `sshTunnel` 隧道：数据库仅内网可达时，经绑定的 SSH 服务器做本地端口转发（`SshTunnel`，基于 SSH.NET `ForwardedPortLocal`），MySQL 密码只发给 MySQL、不落在跳板机；`tunnelServerId` 为空时按拓扑 `canAccess` 关系自动推导跳板。
- **驱动抽象**：`IDatasourceDriver` + `DatasourceDriverRegistry`，按 `datasource.type` 分发；提供 `MySqlDriver`（MySqlConnector）。扩展新类型（如 Redis）只需实现驱动并登记。
- **连接提供者**：`MySqlConnectionProvider` 统一建连（直连/隧道），返回 `IDatasourceSession`。
- **SQL 安全过滤**：`SqlFilterService` —— `mysql_query` 仅放行 SELECT/SHOW/EXPLAIN/DESC/WITH 单条只读语句；`mysql_execute` 中 DROP DATABASE/TABLE、无 WHERE 的 DELETE/UPDATE、TRUNCATE、GRANT 等直接拒绝，其余敏感写语句需用户桌面确认。
- **SQL 审计**：`SqlAuditLog` + `AuditLogService` 记录全部 SQL 操作（含被拒绝/被驳回），落盘 SQLite `%APPDATA%\LitSSH\audit.db`（`SqlAuditLogs` 表）。
- **拓扑模型**：节点 `ssh:` / `ds:` / `app:`，关系 `runsOn`（应用运行在服务器）、`connectsTo`（应用连数据库）、`canAccess`（服务器可访问数据库）；人工声明（`config.json` 的 `relations`）与自动发现（`TopologyEdges` 表）双轨合并。
- **拓扑发现**：`TopologyService.DiscoverAsync` 扫描 SSH 服务器上的 java 进程、ESTAB 网络连接、配置文件 JDBC 地址，并用 MySQL `SHOW PROCESSLIST` 反查客户端 IP。
- **凭据加密**：`ISecretProtector` / `DpapiSecretProtector`（Windows DPAPI，`enc:` 前缀）；`ConfigService` 读写双向迁移，磁盘无明文密码；工具入参只用 `datasourceId`，出参永不包含密码。
- **新增 10 个 MCP 工具**（工具总数 11 → 21）：
  - 数据源：`list_datasources`、`get_datasource_status`、`get_sql_history`
  - MySQL：`mysql_query`、`mysql_execute`、`mysql_explain`、`mysql_diagnostics`
  - 拓扑：`get_topology`、`get_asset_dependencies`、`discover_topology`
- **WPF 数据源管理界面**：`DatasourceManageWindow`（数据源 CRUD + 拓扑关系编辑）与 `DatasourceEditWindow`（含访问模式切换、隧道跳板下拉），主窗口新增"数据源与拓扑关系管理"入口。

#### 升级 Phase 1（安全 / 运维 / 质量）

- **测试工程**：新增 `tests/LitSSHmcp.Tests`（xUnit），覆盖 `SqlFilterService`、`CommandFilterService`、`PathPolicy`、`SecurityOptionsProvider`（热更新）、`DpapiSecretProtector`、`ConfigMigrator`、`AssetNode`，共 40 个用例。
- **MCP 工具注解**：为全部 21 个工具标注 `ReadOnly` / `Destructive` / `Idempotent` / `OpenWorld`（如 `list_*`/`get_*`/`mysql_query` 为只读，`execute_command`/`execute_with_sudo`/`upload_file`/`mysql_execute` 为破坏性），便于客户端向用户提示风险。
- **日志文件**：MCP 日志按天写入 `%APPDATA%\LitSSH\logs\mcp-YYYYMMDD.log`（保留 7 天），stderr 保留。
- **配置版本与迁移**：`AppConfig.schemaVersion` + `ConfigMigrator`；旧配置在启动时自动补写版本号。
- **公共路径与产物**：新增 `ConfigPaths` / `AppConfigJson` 统一 `%APPDATA%\LitSSH` 路径与 JSON 选项。

#### 升级 Phase 2（安全 / 可视化 / 运维）

- **SSH 主机密钥校验（TOFU）**：新增 `ISshKnownHostsStore`（`%APPDATA%\LitSSH\known_hosts.json`）；`SshClientFactory` 经 `HostKeyReceived` 校验指纹，`security.sshHostKey.mode = tofu|strict|off`（默认 tofu：首连记录、指纹变化即拒绝），SSH 与 MySQL 隧道连接均生效。
- **拓扑发现路径白名单**：`security.discovery.allowedSearchPaths`；`discover_topology` 仅允许白名单内路径，越界返回 `path_not_allowed`，留空时使用白名单默认值。
- **按目标限流**：`security.limits`（`maxConcurrentPerTarget` / `maxCallsPerMinutePerTarget`）；新增 `ITargetLimiter`，对 SSH 命令/提权/文件传输、MySQL 连接、拓扑发现按目标限流，超出返回限流错误。
- **应用(app)管理界面**：新增 `ApplicationManageWindow`，可视化维护 `applications[]`（名称/类型/端口/主机/描述），主窗口新增入口。
- **审计查看窗口**：新增 `AuditWindow`（命令 / SQL 两个页签、按服务器/数据源筛选、导出 CSV），主窗口新增入口。
- 单元测试新增 `SshKnownHostsStore`、`TargetLimiter` 用例（总数 47）。
- 说明：**C4 取消支持受 SDK 限制未实现** —— ModelContextProtocol 2.2.0 未向工具处理器暴露 `CancellationToken`，待 SDK 提供后接入。

#### 升级 Phase 3（审计治理 / 可视化 / 运维）

- **修复：拓扑关系无法编辑** —— 关系列表选中后回填编辑框，新增"更新选中"（此前只有添加/删除）；备注列改为原始值+展示值分离。
- **`health_check` 自检工具**（工具总数 21 → 22）：检查配置可读、审计库可写、已知主机库可读，可选测试指定服务器/数据源连通性。
- **审计治理（A6/C5）**：新增 `security.audit`（`storeSqlText` / `maskLiterals` / `retentionDays`）；`SqlRedactor` 对 SQL 字面量脱敏；`AuditLogService` 按保留天数自动清理超期记录。
- **安全设置页（B2）**：`SecuritySettingsWindow` 可视化编辑命令/SQL 过滤、文件传输策略、主机密钥模式、发现路径、限流与审计策略（改动经 `SecurityOptionsProvider` 热生效）。
- **拓扑可视化（B3）+ 自动发现入口（B4）**：`TopologyWindow` 按关系类型表达——`runsOn` 的应用**内嵌在所属服务器区块内**（可直接看出服务器上运行了哪些服务），`connectsTo`/`canAccess` 以带类型标注的连线绘制，人工关系实线、自动发现虚线；内置"自动发现"按钮。
- **配置导入/导出（B6）**：主窗口支持导出（可选脱敏，绝不含明文密码）与导入（导入后自动重新加密）。
- **服务器详情增强（B7）**：显示上次连接时间与该服务器可访问的数据源。
- 说明：**D5（SshService 拆分）** 属较大结构重构，风险较高，暂缓；`ConfigService` 内存缓存在热更新需求下以 `SecurityOptionsProvider` 的 mtime 缓存先行覆盖。

#### 升级 Phase 4（拓扑可视化增强 / 界面重组）

- **拓扑可视化增强**：`runsOn` 现在同时支持**应用与数据库**——两者都内嵌在所属 SSH 服务器区块内（服务器下标注 `N 应用 / M 数据库 (runsOn)`），可直观看到一台服务器上运行的多个应用与其 MySQL；节点标题附带端口（如 `订单库 :3306`、`web-01 :22`）；支持多个应用连接同一数据库；数据库运行在所连服务器上时省略冗余 `canAccess` 连线；**鼠标悬浮节点显示详情**（类型/主机/端口/账号/数据库类型/默认库/访问模式/描述/标签，密码等敏感信息不展示）；修复应用节点被误用数据库配色的问题。
- **管理功能拆分**：原"数据源与拓扑关系管理"拆为 **"数据源管理"**（`DatasourceManageWindow`）与 **"拓扑关系管理"**（`TopologyManageWindow`，关系增删改、节点/类型下拉、选中回填）。
- **主界面重排**：`MainWindow` 顶部新增分类菜单（**资产**：数据源/应用/拓扑关系/资产拓扑图；**安全**：安全设置；**审计**：审计日志；**配置**：导出/导入），主体改为三栏（服务器列表 / 服务器详情 / 命令执行与最近活动），去掉原先堆叠的按钮。
- **主界面重构（轻量 SSH 客户端风格）**：左侧服务器列表改为**两行紧凑项**（名称 + `主机:端口`，不显示描述、宽度自适应列表区）；移除常驻的服务器详情面板（信息在编辑对话框查看）；双击或「连接」在右侧打开**会话标签页**（命令输出/输入/最近活动/测试连接/关闭），空白时显示引导提示。
- **列表交互统一（右键菜单）**：服务器列表顶部只保留 添加/刷新，连接/编辑/删除 移入条目右键菜单（双击连接）；数据源管理同样保留 添加/刷新，测试连接/编辑/删除 移入右键菜单，**双击条目编辑**；应用管理改为与数据源一致（列表 + 添加/刷新 + 右键编辑/删除），并新增 `ApplicationEditWindow` 对话框编辑（原内嵌表单移除）。
- **审计日志模糊查询**：新增"命令关键字"输入框，`GetLogsAsync`/`GetSqlLogsAsync` 支持 `keyword`（`LIKE`）参数，命令审计按 `Command`、SQL 审计按 `Sql` 模糊匹配。
- **菜单新增「MCP工具说明」**（配置之后）：新窗口 `McpToolsWindow` 展示 MCP 接入说明（stdio 配置样例）、意图 → 工具路由表与全部 22 个工具的参数/返回/使用要点；左侧按章节/工具浏览、可搜索，右侧等宽字体显示，支持 **复制本节 / 复制全部说明**（可直接粘贴进 AI 智能体提示词）。
- **工具说明"双向同步"约定**：内容以 `docs/TOOLS.md` 为唯一事实来源并嵌入 App（`EmbeddedResource`）；同步约定注释写入 ① `McpServer/Tools/*.cs` 文件头（9 个工具类）② `Program.cs` 的 `WithTools<T>()` 注册处 ③ `McpToolsWindow.xaml.cs` 文件头 ④ `LitSSHmcp.App.csproj` 嵌入声明 ⑤ `UsageGuideTools.GetUsageGuide()` 内置清单，并在 `docs/TOOLS.md` 顶部写明三处必须一起改。

#### 升级 Phase 5（授权弹窗改造）

- **授权确认弹窗升级**：把原来的 Win32 `MessageBox`（P/Invoke）替换为自绘的 WinForms 对话框（`ApprovalDialog`）——**强制置顶**（可配置）、**无操作 120 秒自动拒绝**（可配置）、深色标题栏 + 命令内容滚动框 + "复制内容"按钮；环境不支持显示时按拒绝处理（fail-closed）。
- **依赖变化**：`LitSSHmcp.McpServer` 目标框架由 `net8.0` 调整为 `net8.0-windows` 并启用 WinForms；弹窗在独立 STA 线程上显示，多个授权请求串行化。
- 新增配置 `security.approval = { style, timeoutSeconds: 120, topMost: true }`。`style` 可选：`process`（**推荐**，启动独立子进程显示审批弹窗，规避宿主的隐藏窗口问题，带超时）、`dialog`（进程内自绘）、`native`（原生置顶 MessageBox，无超时）。自绘/子进程失败自动回退原生；子进程通过退出码回传结果（0=允许/1=拒绝/2=超时），请求经临时文件传递（不含密码）。失败细节写入日志。

### 安全

- **文件传输策略落地**：`FileTransferTools` 现校验 `fileTransfer.enabled` 及 `allowedLocalPaths` / `allowedRemotePaths` 白名单（此前字段已定义但未生效），越界返回 `path_not_allowed` / `file_transfer_disabled`；新增 `PathPolicy` 做路径规范化与 `..` 穿越拦截。
- **安全过滤器热更新**：`CommandFilterService` / `SqlFilterService` 改为经 `ISecurityOptionsProvider` 按配置文件 mtime 读取，修改 commandFilter/sqlFilter/fileTransfer 后**无需重启** MCP。
- **审计库加固**：`audit.db` 启用 WAL 与 `busy_timeout`，并为时间/服务器/数据源建索引。

### 修复

- **WPF "添加"按钮卡死**：`DatasourceEditViewModel` 构造函数曾在 UI 线程上同步阻塞 `LoadConfigAsync().GetAwaiter().GetResult()`，`await` 续延需回到被阻塞的 Dispatcher → 经典 sync-over-async 死锁。修复为构造函数接收 `SshServerConfig[]` 参数、`Add()/Edit()` 改为 `async` 先 await 加载再构造窗口。已用 WPF Dispatcher 线程测试桩复现（旧模式挂起）并验证修复后不卡死。
  约定已写入 [docs/ARCHITECTURE.md](ARCHITECTURE.md) 线程模型章节：ViewModel 禁用 `.Result` / `.GetResult()`。

### 变更

- **资产拓扑可视化编辑器（合并「拓扑关系管理」+「资产拓扑图」）**：两者合并为一个可交互窗口（菜单 **资产 → 资产拓扑(可视化编辑)**）。
  - **无限画布**：滚轮**缩放**、按住**中键/右键拖动平移**、工具栏 `＋/－/适应/100%`、`Ctrl+0` 重置；**关系属性面板可拖动**标题栏移动；
  - **拖动落点判定容器归属**：节点完全落入服务器 → 自动建立 `runsOn`（经 `RelationRules` 校验，含 runsOn 唯一性，不合法则禁止并回退）；从服务器内拖出 → **弹窗确认**删除 `runsOn`；服务器间直接拖动 → 确认后删除旧关系并新建；**禁止任何节点与服务器部分重叠**（必须完全在内或完全在外）。
  - **调整大小同样做重叠校验**：缩放到触碰服务器区域时即时停止（`ResizeSelected` 中 `ServerOverlapInvalid` 守卫），松开鼠标再 `ValidateResize` 复核，非法则还原；托管子节点仅在所属服务器内允许缩放。
  - **修正**：托管子节点在加载时统一 `ClampChild` 限制在所属服务器内（消除"框外却仍提示移出"的错觉）；拖出删除 `runsOn` 时同时清理自动发现缓存；仅当拖动前确实在服务器内才提示移除。
  - **服务器调整不得接触任何节点**：`ServerOverlapInvalid` 对服务器检查与所有其它节点的相交（仅允许完全包含自己的托管子节点）；普通节点仍禁止与服务器重叠。
  - **runsOn 托管节点用点线边框**渲染（应用橙/库绿，`1 3` 点线），与选中框的蓝色长虚线明显区分。
  - **画布网格背景**（20px `DrawingBrush`，随缩放平移）；**节点移动与调整大小自动吸附 10px 网格**。
  - **手动锚点不漂移**：`ComputeRoute` 对已设置端点的边按固定侧取中点，快/慢路由一致（拖节点时端点固定在该侧）。
  - **拖动性能**：拖动过程中跳过过桥（hops）计算，且网格吸附后位置未变化时跳过重算，缓解卡顿。
  - **画布网格铺满视口**：网格改用独立 `GridLayer`（`DrawingBrush.Transform` 随缩放/平移同步），修掉"缩放/平移后边缘无网格"。
  - **平移改为空白处左键拖动**（取消中键/右键平移）。
  - **悬停光标反馈**：指针在节点四边/四角显示对应缩放光标、在节点上显示移动光标。
  - **节点右键 → 查看/编辑关系**：新增 `NodeRelationsWindow`，列出该节点相关的全部关系（手动/自动发现），可编辑类型与备注（经 `RelationRules` 校验）或删除；关闭后主拓扑刷新。
  - `NodeRelationsWindow` 列表以**节点名称**显示（悬浮显示完整 ID），**当前节点红色加粗**区分；关系图节点名带端口。
  - **解除 `runsOn` 后节点归位**：`RelocateOrphanedStandalone` 将仍落在服务器区块内的独立节点移动到**就近空白处**并持久化布局。
  - **连接点可重叠**：同走廊连线不再分道错位（`LaneGap=0`），多条边在同一连接点重合，观感更整洁。
  - **连线不再标注 canAccess/connectsTo**（仅 `relatedTo` 等保留类型文字）。
  - **服务器内节点移动/缩放更顺手**：托管子节点在所属容器内允许部分重叠、松开夹回容器（`ServerOverlapInvalid` 跳过自身父容器）；命中顺序改为 **叶子节点 → 连线 → 服务器区块**（`HitLeaf/HitEdge/HitBox`），解决连线盖住节点导致拖不动。
  - **方向键移动选中节点**：`NudgeSelected`（10px 吸附 + 容器/重叠校验，可撤销）。
  - **修复服务器内节点缩放无反应**：连接端口此前与四边中点的缩放手柄**重合**且优先命中，导致抓住边中点变成"建边"而非缩放；现把端口移到节点**外侧**（不再重叠），并按 **叶子节点(2px 容差) → 连线 → 服务器区块**顺序命中。
  - **修复回退后选中框错位**：`RejectDrag` 先刷新选中框再重建连线；鼠标松开后统一 `RefreshSelection()`，落点回退时选中框回到真实位置。
  - **缩放/建边交互拆分**：缩放只在**四角**手柄；每边中点只保留**连接圆点**（建边），两者不再重叠，鼠标形状分别为对角线缩放/十字。
  - **拓扑拖动卡顿优化**：`topology-layout.json` 改为**内存缓存**（此前 `RebuildEdges` 每次鼠标移动都读盘+反序列化，是主要卡顿源）；拖动过程连线重算做**节流 + 尾部补算**（约 25ms）。
  - **可选性能日志**：设置环境变量 `LITSSH_PERF=1` 后，`CommitLayout` / `RebuildEdges(fast|full)` 耗时写入 `%APPDATA%\LitSSH\topology-perf.log`（`PerfLog`，未启用零开销）。
  - **拖动/缩放一致性修复**：缩放也走节流（`RequestFastRebuild`）并跳过吸附后未变化；`CommitLayout`/`RejectDrag` 取消残留的"尾部补算"定时器，避免松手后用快速 Z 形路由覆盖最终避障路由；完整路由在 Z 形无障碍时直接采用（`PathClear`），使拖动预览与松手后的连线路径**不再跳变**。
  - **拖动连线原地更新（性能）**：`GraphEdgeVm` 实现 `INotifyPropertyChanged`；拖动过程改为 `RebuildIncidentEdges` **只重算并原地更新与拖动节点相连的少数连线**，不再每帧 `Edges.Clear()` 全量重建，消除 WPF 容器抖动。
  - **真正无限画布**：解除节点移动的 `>=0` 限制，可越过左/上边界（网格随平移/缩放铺满视口）。
  - **拓扑路由引擎重构（性能 + 抽象）**：抽出无状态 `TopologyRouteEngine`，集中路由/避障/过桥/渲染几何，`TopologyViewModel` 委托调用：
    - **P3** 避障 A* 由 4×4 次降为**多源多目标一次**；
    - **P1** 障碍**空间索引**（均匀网格）加速线段穿障查询；
    - **P2** 过桥检测加**包围盒剪枝**；
    - **P0** 每条边/过桥各用一个 `ItemsControl` + 每项一个 `<Canvas>` 内叠加 `Path`（折线/圆点/箭头、白盘/补段/拱线），`Edges`/`Hops` 由 7 个 `ItemsControl` 降为 2 个（保持绝对坐标，端点正确落在节点上）。
  - **性能优化 P0+P1+P2（测量基线 → 消除交互浪费 → 路由共享上下文）**：
    - **P0 测量**：`PerfLog` 扩点（拖动会话 `DragSession` 汇总、`Zoom`/`Load` Scope）；引擎基准测试 `TopologyRouteEngineBenchmarkTests`（200 障碍 × 300 路由）；DEBUG 下工具栏「压测」按钮 + `LoadSynthetic` 合成图（约 208 节点/340 边，`stress:` 前缀 id、不落盘布局）。
    - **P1 消除每次鼠标移动的隐性浪费**：手柄/端口改为 **INPC 就地更新**（`UpdateSelectionRect` 每帧集合变更 10 次 → 0，不再重建容器）；`StrokeFor` 加静态 `ConcurrentDictionary` 画笔缓存（同引用使 WPF 跳过描边失效）；`GraphEdgeVm`/`SelX..SelH` 全部加**等值守卫**，`RebuildIncidentEdges` 中路径未变时跳过几何重建（`SamePoints`）；连线标签拆出独立 `Labels` 集合（无标签的边不再产生空容器）。
    - **P2 路由共享上下文**：新增 `TopologyRouteEngine.RouteContext`——障碍网格索引与排序坐标**一次构建、多条边共享**，端点盒容器排除改为查询期按候选应用（`ObstacleIndex.SegmentClear(..., fromRect, toRect)`），消除每边的过滤分配与索引重建；`BuildRoute` 提供共享上下文重载，旧签名封装为便捷入口（既有测试不受影响）。基准：**共享上下文 0.9 ms / 300 路由**（逐边旧路径 93.6 ms，逐边等价性断言保证输出一致），clear 300/300。
    - **压测图防污染**：`CommitLayout`/`RelocateOrphanedStandalone`/`EndAnchorDrag` 在合成图模式下不写 `topology-layout.json` 也不改布局缓存。
    - **新增测试** `TopologyRouteContextTests`（网格探针逐段等价、容器透明 vs 未过滤阻挡、共享上下文 = 逐边构建、端点锚定不回归），用例 166 → **170**。
  - **缩放/拖动卡顿优化（帧率 + 语义修正）**：
    - **服务器缩小不再被子节点挡住**：`ResizeSelected` 改为先按候选矩形用新纯函数 `ClampChildTo` 预计算子节点收紧位置（容器过小时按 `Clamp` 的 (min+max)/2 居中，不产生反向钳制），`ServerOverlapInvalid` 服务器分支跳过自身托管子节点——缩放全程顺滑、子节点自动跟随收紧（原语义：子节点部分越界 → 整个缩放被拒绝、原地停住，表现为"卡住/一顿一顿"）；与其它节点相交"碰到即停"的规则不变。
    - **每个鼠标移动零分配**：`ResizeSelected` 无变化早退提到 overlap 校验之前（网格吸附后多数微移动直接返回）；`ServerOverlapInvalid` 不再每次 `ToArray()`（`_allItems` 缓存于 `ApplyGraph`）；`FindItem` 三次 `FirstOrDefault` → `_itemsById` 字典；`_parentOf` 每帧反查 → `_childrenOf` 索引（`MoveNode`/`ResizeSelected`/`RebuildIncidentEdges` 的 `Where/Select/ToArray/HashSet` 全部移除）；`GraphEdgeVm.Key` 建边时算一次，拖动热路径不再做 `$"{from}|{type}|{to}"` 字符串插值。
    - **帧率探针**（`LITSSH_PERF=1` 时才启用）：拖动/缩放期间挂 `CompositionTarget.Rendering`，松手写 `FrameProbe(kind): frames= avg= max= events= tier=`（`tier=0` 为软件渲染/RDP）；与既有 `DragSession`（VM 每帧耗时）对照即可区分瓶颈在 VM 还是渲染层。
    - 单测新增 `TopologyLayoutGeometryTests`（`ClampChildTo` 内夹/不动/贴边/容器过小居中），用例 170 → **174**。
  - **修复拖动/缩放累积漂移（绝对锚定模型）**：移动/缩放曾用「上次位置 + 事件增量 + 10px 吸附」迭代推进，未跨吸附边界的增量被舍入**永久丢失**，来回移动鼠标后节点与鼠标的相对位置逐渐变化（跟不上鼠标）。现改为**绝对锚定**：
    - `BeginNodeDrag(id, anchor)` 记录鼠标锚点；`MoveNode(id, Point)` 每次从「拖动快照起点 + 相对锚点总位移」直接推导新位置（含子节点同步），`ResizeSelected(handle, Point)` 同理从 `_dragStartRect` 用新纯函数 `ComputeResize`（对边固定、最小尺寸钳制、10px 吸附）计算——结果只取决于总位移，**与事件路径无关，往返不漂移**；
    - 缩放被其它节点挡住时**以当前几何重锚**（吸收被挡位移），反向拖动无死区；方向键 `NudgeSelected` 改走增量路径 `MoveByDelta`（±10px 整数，吸附无损）；
    - 拖动状态统一 `ClearDragState()`（快照/缩放基准/锚点），`ValidateResize` 等路径补齐清理；
    - 新增单测 `TopologyLayoutGeometryTests.ComputeResize_*`（起点+总位移、振荡无漂移回归、对角锚定对边不动、最小尺寸钳制），用例 174 → **179**。
  - **选中节点四边可直接拉伸缩放**：
    - 四边内侧 6px 新增缩放带（两端各避开角手柄 8px，角手柄仍优先用于对角缩放）：悬停显示 `SizeNS/SizeWE` 光标，按住拖动即按 `ResizeSelected` 的 `n/e/s/w` 手柄调整该侧（对边固定、子节点自动收紧，复用绝对锚定模型）；中心区域仍是移动、未选中节点行为不变；
    - **建边端口移到节点外侧**（圆心距边界 `PortRadius+Gap=8px`，与边带互不重叠；`PortCenterFor` 统一计算，建边预览线也从外侧圆心出发）——原来端口圆心压在边界中点上，与拉边交互在同一位置冲突；
    - 新增单测 `EdgeHandleAt`（四边命中/角与中心排除/过小节点无边带）与 `PortCenterFor`（外侧间隙、命中盒不越界），用例 179 → **183**。
  - **工具栏精简**：移除顶部「删除选中关系」按钮（画布点选连线 + Delete 键 / 关系属性面板「删除」均可删除，`OnDeleteEdge` 仍服务于面板与快捷键）；「＋、－、适应、100%、刷新、压测」6 个按钮合并为最右侧「更多操作 ▾」下拉菜单（`ContextMenu`，100% 标注 Ctrl+0，压测项仅 DEBUG 显示），工具栏只保留「重新自动布局 / 自动发现」两个平铺按钮。
  - **导出图片（更多操作 → 导出图片…）**：把画布有效区域（`ContentBounds()` 全部内容 + 24px 边距，忽略当前缩放/平移）渲染为 PNG 保存——`RenderTargetBitmap` 临时以 `TranslateTransform` 对齐区域原点（2x 分辨率、超大图退化 1x），导出前隐藏选中框/手柄/端口/连线高亮并用 `#FAFAFA` 打底，导出后恢复选中状态与画布变换。新增 `RenderTargetExportTests`（STA 线程验证根级 RenderTransform 生效 + 世界坐标渲染，即导出平移截取的核心前提），用例 183 → **184**。
  - **新增测试工程** `tests/LitSSHmcp.App.Tests`（net8.0-windows）对 `TopologyRouteEngine` 做单元测试（Z 形、避障、快/慢一致性、过桥、简化）。
  - 节点**拖动/缩放**（拖服务器带动其内子节点；子节点限制在容器内；8 手柄缩放 + 最小尺寸）；**10px 网格吸附**；
  - 选中节点后从**边中点端口拖拽到目标节点建边**（按节点类型自动推断 `runsOn`/`connectsTo`/`canAccess`/`relatedTo`，并经 `RelationRules` 逻辑校验，**含 `runsOn` 每节点只指向一台服务器**）；
  - **点选连线**后弹出**关系属性面板**（右上角）：可改**关系类型**、编辑**备注**（`RelationConfig.Note`）并保存（经逻辑校验）；自动发现边仅可删除；
  - **点选连线删除**：手动关系删 `config.Relations`；自动发现边清理 `TopologyEdges` 缓存（新增 `ITopologyStore.RemoveEdgeAsync`）；**连线在服务器节点内也可直接点选**（选线优先于选节点，4px 容差）；
  - 选中连线后可拖动**两端锚点**指定接边位置（`AnchorSpec`，路由按固定侧走）；
  - **Ctrl+Z 撤销 / Ctrl+Y 重做**；**「重新自动布局」**清除手动布局；
  - 手动布局（节点位置/尺寸、端点锚点）持久化到 `%APPDATA%\LitSSH\topology-layout.json`（`TopologyLayout` + `ITopologyLayoutStore`），与语义配置 `config.Relations` 分离。
  - **修复**：拖动时边被简化为直线导致 `LabelPoint` 越界崩溃（折线点 <3 时改用首尾中点）；A* 索引与单条边计算加了防御，鼠标回调异常改为弹窗提示不崩溃。
- **runsOn 唯一性校验**：`RelationRules.TryValidate(from,to,type, existing, out error)` —— 一个应用/数据库只能运行在一台服务器上，已 runsOn 别的服务器时拒绝并提示（接线到编辑器与「拓扑关系管理」旧逻辑）。

- **拓扑可视化连线优化（阶段1~4）**：连线采用**避障正交路由**——在障碍(所有节点盒,Hanan 栅格)上跑 **A\***，**不直穿其它节点**；**源/目标端点从四边中点候选里择优选**（任意边/点皆可作端点），且**源/目标所在的外层服务器区块对其子节点透明**（不作为障碍，从而能进入嵌套区块到达被 `runsOn` 的数据库/应用）；找不到路径回退 Z 形。拐角**圆角化**；起点圆点、终点箭头（随类型更明显，自动发现略小且虚线）；同一走廊多条边分道错位（含对向）减少并线；交叉处**过桥**（白遮罩 + 下层补段 + 上层拱线）；层级为「服务器区块背景之上、叶子节点之下」。
- **MCP 全局开关**：`security.enabled`（安全设置窗口顶部「启用 MCP 服务」勾选框）——关闭后通过请求中间件（`McpGlobalSwitch` 注册到 `McpServerOptions.Filters.Request.CallToolFilters`）**拒绝所有工具调用**（返回 `isError=true` 与提示文本；工具仍会列出）。读取 `ISecurityOptionsProvider`（按配置文件 mtime 热更新），**无需重启** MCP。
- **拓扑关系逻辑校验**：新增 `RelationRules.TryValidate`（Core）——`runsOn` 只能 应用/数据库→服务器、`connectsTo` 只能 应用→数据库、`canAccess` 只能 服务器→数据库、`relatedTo` 通用但禁止自环、未知类型拒绝；「拓扑关系管理」添加/更新关系时校验并**弹窗**提示原因（如 `ssh:A --runsOn--> ssh:B` 会被拒绝）。
- **删除资产的关系清理与校验**：删除 SSH 服务器 / 数据源 / 应用时，先统计引用它的**手动关系记录**并在确认框列出数量与明细（存在则一并删除；不存在也会提示），同时清理引用该节点的**自动发现拓扑边**（`ITopologyStore.RemoveEdgesByNodeAsync`），避免残留孤立关系/节点。
- **Docker 应用管理（A：登记 + 安全规则）**：应用管理新增 **Docker 容器名** 字段（`ApplicationConfig.ContainerName`，类型填 `docker`），并登记 `runsOn` 关系后，AI 即可用 `ssh_execute_command`（必要时 `ssh_execute_sudo`）走 `docker ps/logs/restart/exec` 管理容器——**无需新增 MCP 工具**。默认安全规则补充：拒绝 `docker system prune`/`docker volume prune`/`docker network prune`/`docker volume rm`/`docker service rm`/`docker swarm leave`/`docker run --privileged`/`docker run -v /`；敏感（需桌面确认）`docker rm/rmi/kill/stop/restart/run/exec/cp/compose down/compose rm`。
- **Docker 拓扑发现（B）**：`topology_discover` 增加 `docker ps` 容器扫描（`DiscoveryResult.DockerContainers`），按 `ContainerName`/应用名匹配已登记应用，**自动建立 `app --runsOn--> ssh` 关系**。
- **修复：`redis_read`/`redis_execute` 的 `outputSchema` 非法导致部分 MCP 客户端拒绝加载**：动态字段 `result`（`object?`）被 SDK 生成为布尔 schema（`true`），客户端校验报 `outputSchema.properties.result: Invalid input`；改为把结果序列化为 **JSON 文本字符串**（schema 为 `["string","null"]`）。`json_query` 等其余工具的 schema 不受影响。
- **审计防篡改（HMAC 哈希链）+ 超期记录永久归档**：`AuditLogService` 新增哈希链（`AuditChain` 表；有本地签名密钥时用 HMAC-SHA256，密钥 DPAPI 保护于 `audit.db.key`，否则退化为 SHA-256）；写审计时在同一事务内追加链记录。保留策略由"删除"改为**移动到历史表** `AuditLogsHistory`/`SqlAuditLogsHistory`（永久保留；链不删除、记录 Id 不变，故整链始终可验证）。新增 `IAuditLogService.VerifyChainAsync()` 与模型 `AuditVerifyResult`；App「审计日志」新增 **校验完整性** 按钮与 **含归档** 筛选（`GetLogsAsync/GetSqlLogsAsync(includeHistory)`）。
- **审批带外通道（CLI/IPC，无 HTTP）**：`IApprovalService` 改为多通道分发 `ApprovalService` —— `security.approval.channels`（默认 `["desktop"]`，可加 `cli`）各通道并发等待，**首个给出决定者生效**，全部弃权或超时→拒绝（fail-closed）。`cli` 通道写 `%APPDATA%\LitSSH\approvals\pending-<id>.json` 并轮询决策文件；新增 CLI `litssh approvals` / `litssh approve <id>` / `litssh deny <id>`（适配无桌面/headless）。安全设置窗口新增「审批通道」配置；共享文件格式 `ApprovalFileStore`（Core）。
- **`LITSSH_DATA_DIR` 数据目录覆盖**：`ConfigPaths` 支持环境变量覆盖 `%APPDATA%\LitSSH`（测试隔离/便携部署）。
- **MCP 协议集成测试 + 数据目录隔离**：新增 `tests/LitSSHmcp.McpServer.Tests/McpProtocolIntegrationTests.cs` —— 以 stdio 真实启动服务器进程、走 `initialize → tools/list → tools/call` 全链路，校验 29 个工具、`inputSchema`/`outputSchema` 存在性、结构化错误（`mysql_query` 非法数据源）与 text(JSON) 兼容层、`mcp_self_check` 成功。
- **结构化输出（全量，除使用指南）**：**除 `mcp_usage_guide`（使用指南文档）外，全部 28 个工具**改为返回 DTO（`[McpServerTool(UseStructuredContent = true, OutputSchemaType = ...)]`，DTO 集中在 `McpServer/Services/ToolResults.cs`），由 SDK 生成 `outputSchema` 并**同时**产出 `structuredContent` 与等价 text(JSON)（向后兼容）。契约变化：`ssh_list_servers`/`datasource_list`/`ssh_get_command_history`/`datasource_get_sql_history`/`ssh_list_files` 由裸数组改为 `{ success, count, ... }`/`{ success, path, files }` 包裹；命令类返回 `output`/`error`（替换原 stdout/stderr 描述）；字段统一 camelCase（拓扑/SSH/数据源）。
- **数据源治理（每数据源，可选）**：`DataSourceConfig` 新增 `MaxRows`（查询上限）、`TimeoutSeconds`（命令超时）、`ReadOnly`（拒绝写操作）、`WriteApproval`（`Always`=一律桌面确认 / `AutoApprove`=受信任自动放行）；`mysql_*`/`postgres_*`/`redis_*` 均按此治理（只读返回 `readonly_datasource`；自动放行仅跳过桌面确认，仍受硬拦截规则约束）。桌面数据源编辑窗口新增对应控件。
- **查询结果列级脱敏**：新增 `SecurityConfig.Masking`（规则 `{ column: 列名正则, mode: full|email|phone|last4 }`）与 `ResultMasker`；`mysql_query`/`postgres_query` 返回前按列名对单元格脱敏。安全设置窗口可编辑（每行 `列名正则=模式`）。
- **PostgreSQL 数据源（工具总数 25 → 29）**：新增 `PostgresConnectionProvider` + `PostgresDriver`（Npgsql；直连/SSH 隧道；诊断采集 `pg_stat_activity`/`pg_stat_database`/`pg_stat_replication`）与工具 `postgres_query` / `postgres_execute` / `postgres_explain` / `postgres_diagnostics`（分组 `postgres`，路由键与「工具分组设置」一致）；数据源类型新增 PostgreSQL（端口默认 5432）。驱动注册续用 `IEnumerable<IDatasourceDriver>`，新增类型零改注册表。
- **MCP 协议增强（会话指令 + 进度通知）**：`initialize` 返回 **Server Instructions**（`Services/McpServerInstructions.cs`：按意图选组、ID 获取、写操作需桌面确认、不含密码；详细路由仍由 `mcp_usage_guide` 提供）；`ssh_upload_file`/`ssh_download_file` 通过注入的 `IProgress<ProgressNotificationValue>` 向前端推送 `notifications/progress`（字节数/百分比）。SDK 也支持 structured output（`[McpServerTool(UseStructuredContent=..., OutputSchemaType=...)]` + 返回强类型对象），因需把全部工具改为返回 DTO，列为后续项。
- **工具清单单一来源 + 一致性守门测试**：`get_usage_guide` 的 `tools` 清单不再手写，改为对程序集中所有 `[McpServerTool]` 方法的 `Name` + `[Description]` **反射生成**；新增测试工程 `tests/LitSSHmcp.McpServer.Tests`（net8.0-windows）断言"已注册工具 ↔ `docs/TOOLS.md` ↔ `get_usage_guide` 清单"三者集合一致、工具数 25、分组键等于 `ToolGroups.All`——工具增删改名后漏更新文档会直接测试失败。
- **驱动注册可扩展（DI 化）**：`DatasourceDriverRegistry` 构造改为注入 `IEnumerable<IDatasourceDriver>`，按驱动自报的 `Type` 建立索引；`Program.cs` 以 `AddSingleton<IDatasourceDriver, MySqlDriver/RedisDriver>()` 注册。新增数据源类型只需实现驱动并注册 DI，不再改动注册表构造函数（App 端 `AppServiceFactory`/`DatasourceEditViewModel` 同步）。
- **「MCP工具说明」窗口分组与「工具分组设置」对齐**：`docs/TOOLS.md` 结构调整为 `## 概览与接入` + 8 个分组章节，分组标题格式 **`## <中文名>（<分组键>）`**，分组键与 `config.json` 的 `tools.enabledGroups`（App「工具分组设置」）严格一致；`McpToolsWindow` 解析规则改为：`### \`工具名\`` 计为工具、其它 `###` 子标题并入所属章节内容（不再误判为工具/分组）。窗口顶部显示"共 25 个工具 · 8 个分组"，当分组键与 `ToolGroups.All` 不一致时给出告警。
- **工具命名规范与全量重命名（破坏性；测试阶段无历史包袱）**：统一为 **`<域>_<动作>[_<对象>]`** 的 snake_case（域前缀 `ssh`/`datasource`/`mysql`/`redis`/`topology`/`mcp`），并在 `docs/TOOLS.md` 新增「工具命名约定」。旧 → 新映射：
  - `list_servers`→`ssh_list_servers`、`get_server_status`→`ssh_get_server_status`、`test_connection`→`ssh_test_connection`
  - `execute_command`→`ssh_execute_command`、`execute_with_sudo`→`ssh_execute_sudo`、`get_sudo_status`→`ssh_get_sudo_status`、`get_command_history`→`ssh_get_command_history`
  - `upload_file`→`ssh_upload_file`、`download_file`→`ssh_download_file`、`list_remote_files`→`ssh_list_files`
  - `list_datasources`→`datasource_list`、`get_datasource_status`→`datasource_test_connection`、`get_sql_history`→`datasource_get_sql_history`
  - `get_topology`→`topology_get_overview`、`get_asset_dependencies`→`topology_get_dependencies`、`discover_topology`→`topology_discover`
  - `get_usage_guide`→`mcp_usage_guide`、`health_check`→`mcp_self_check`；`mysql_*` / `redis_*` 保持不变。
  工具名改用显式 `[McpServerTool(Name = "...")]`；同步更新描述互引、`UsageGuideTools` 内置清单、App 工具分组提示、README/ARCHITECTURE/`docs/TOOLS.md`。**AI 客户端系统提示词中引用旧名的需同步更新。**
- **工具描述瘦身（降低 AI 上下文与误选）**：25 个工具的 `[Description]` 由"长句式场景枚举"压缩为 1~2 句（做什么 + 关键互斥提示），描述总字符数 **2674 → 1291（−52%）**，单条最长 228 → 115；被移除的长篇"何时用 / 不要用"统一收敛到 `docs/TOOLS.md` 的「意图 → 工具路由表」与 `get_usage_guide`（新增 `file_transfer_guide`），并在工具文件头与 `docs/TOOLS.md` 顶部写明"描述保持精简"的约定。
- **MCP 工具说明意图路由优化**：`[Description]` 改写为"当用户问…时使用"句式，并在易混工具间加反向提示（`list_servers` vs `list_datasources`、`test_connection` vs `get_datasource_status` 等）；`datasourceId` 参数统一标注"可用 list_datasources 列出"；`get_usage_guide` 新增 `datasource_query_guide` 意图表。解决"用户想看 MySQL 列表/测连接时 AI 选错工具"的问题。
- **文档拆分**：README 精简为项目门面（特性/快速开始/示例/CLI），新增 `docs/ARCHITECTURE.md`（架构）、`docs/TOOLS.md`（工具参考与路由表）、`docs/CHANGELOG.md`（本文件）。

## [1.0.0] - 2026-09-19

初始发布（commit `cd3621b`，badge 更新见 `8891f60`）。

### 新增

- **MCP 服务器**：stdio 传输（ModelContextProtocol 2.2.0），11 个工具：
  - SSH：`list_servers`、`get_server_status`、`test_connection`
  - 命令：`execute_command`、`execute_with_sudo`、`get_sudo_status`、`get_command_history`
  - 文件：`upload_file`、`download_file`、`list_remote_files`
  - 指南：`get_usage_guide`
- **安全控制**：命令黑名单/敏感命令桌面确认（Win32 MessageBox）、文件传输审批、命令审计、sudo 提权（CurrentUser/RootUser/CustomUser）、凭据不入 AI 上下文。
- **WPF 管理界面**：SSH 服务器增删改（`MainWindow` + `ServerEditWindow`）。
- **CLI**：`litssh list` / `litssh connect` / `litssh run`。
- **存储**：`%APPDATA%\LitSSH\config.json` 配置 + `audit.db` SQLite 审计。

[未发布]: https://github.com/joolan/LitSSHmcp/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/joolan/LitSSHmcp/releases/tag/v1.0.0
