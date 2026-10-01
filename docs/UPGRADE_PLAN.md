# LitSSH MCP 升级方案（Roadmap）

本文是界面、操作、可视化配置、安全、架构与质量方面的升级设计与实施计划。**现状问题均已在代码中核实**，改动点标注了具体文件。

约定：配置新增字段一律带默认值，保证旧 `config.json` 向后兼容；所有工具返回结构保持 `{ success, status, error }` 不变。

## 进度

- **Phase 1 已完成**（A4 文件传输策略落地、A2 过滤器热更新、D2 配置 schemaVersion+迁移、C1 日志文件、D1 测试工程起步、D4 工具注解；另附带 A6 审计库 WAL+索引）。提交/发布见 [CHANGELOG.md](CHANGELOG.md)。
- **Phase 2 已完成**（A1 SSH 主机密钥 TOFU、A5 发现路径白名单、C3 按目标限流、B1 应用管理界面、B5 审计窗口；测试 47 项）。
  - **C4 取消支持受限**：ModelContextProtocol 2.2.0 未向工具处理器暴露 `CancellationToken`，无法在当前 SDK 版本实现，待升级 SDK 后接入。
- **Phase 3 已完成（部分）**：A6/C5 审计治理（脱敏+保留天数轮转）、B2 安全设置页、B3 拓扑可视化、B4 自动发现入口、B6 配置导入/导出、B7 服务器详情增强、C2 `health_check`（工具总数 22）；同时修复"拓扑关系无法编辑"。
  - **D3 统一错误模型**：以渐进方式推进（统一返回结构延续），未做大范围重写。
  - **D5 SshService 拆分**：暂缓（大范围结构重构，风险较高）。
- 全部 4 个方向（A/B/C/D）的 Phase 1–3 主体已完成；C4 受 SDK 限制、D5 暂缓。

## 后续迭代（Phase 4 / 5）

- **Phase 4（可视化/界面）**：拓扑可视化增强（`runsOn` 支持应用与数据库嵌套、端口展示、节点悬浮详情）、管理功能拆分（数据源管理 / 拓扑关系管理）、主界面按功能分类重排。
- **Phase 5（授权弹窗）**：原生 MessageBox → 自绘置顶弹窗；支持 `security.approval`（`style=process|dialog|native`、超时自动拒绝、置顶），`process` 模式用独立子进程规避宿主隐藏窗口问题。
- 详见 [CHANGELOG.md](CHANGELOG.md)。
---

## 0. 已核实的问题清单

| # | 问题 | 证据 |
|---|------|------|
| 1 | **文件传输策略字段未生效**：`Enabled` / `AllowedLocalPaths` / `AllowedRemotePaths` 已定义但工具从不校验 | `Models/AppConfig.cs:19-26` vs `Tools/FileTransferTools.cs`（仅查存在性/大小/审批） |
| 2 | SSH 主机密钥不校验，存在中间人风险 | `Services/SSH/SshClientFactory.cs`（无 `HostKeyReceived`） |
| 3 | 安全过滤器启动时构建为单例，改配置须重启 MCP | `McpServer/Program.cs:23-35` |
| 4 | 审批只有单个 MessageBox：不能"本会话记住"、无超时、无影响面说明 | `Services/IApprovalService.cs` |
| 5 | `discover_topology` 扫描路径 `searchPaths` 由 AI 任意指定 | `Tools/TopologyTools.cs:39` |
| 6 | 审计库无索引/无 WAL/无轮转；SQL 明文落库 | `Services/Storage/IAuditLogService.cs` |
| 7 | 应用(app)节点只能手改 JSON，无界面 | `ViewModels/DatasourceManageViewModel.cs:145`（下拉含 app，但无维护入口） |
| 8 | 安全策略、文件传输限额无界面 | 仅 `config.json` |
| 9 | 拓扑无可视化，只有关系列表 | `Views/DatasourceManageWindow.xaml:44-90` |
| 10 | 自动发现无界面入口；审计无独立查看窗口 | 仅 AI 工具触发 |
| 11 | MCP 日志只到 stderr，无日志文件 | `McpServer/Program.cs:53-56` |
| 12 | 无并发/速率限制；工具未传 MCP 取消令牌 | `Tools/*.cs`（未接 `RequestContext`） |
| 13 | 无测试工程、无配置版本号；`ConfigService` 硬编码 `new DpapiSecretProtector()`，难测试 | `src/`（无 test 项目）、`Storage/IConfigService.cs:18` |

---

## 1. 安全加固（A）

### A1 SSH 主机密钥校验（TOFU）
- 新增 `Services/SSH/ISshKnownHostsStore.cs`：`%APPDATA%\LitSSH\known_hosts.json`，记录 `host:port -> {algorithm, fingerprint, firstSeen}`。
- `SshClientFactory.Create/CreateSftp` 增加 known-hosts 参数，注册 `HostKeyReceived`：首次记录（TOFU，可弹窗确认）；指纹变化则**拒绝连接**并返回含新旧指纹的错误。
- 新增配置 `security.sshHostKey = { mode: "tofu" | "strict" | "off" }`，默认 `tofu`（升级即生效且不断联）。
- 改动：`SshClientFactory`（签名）、`SshService`、`SshTunnel`、`AppConfig`、WPF 增加"已知主机/指纹"管理界面。
- 风险：需保证错误信息清晰（含目标、旧/新指纹），避免用户误判。

### A2 安全过滤器热更新
- `CommandFilterService` / `SqlFilterService` 改为持有 `IConfigService`，按配置文件 mtime 缓存重建；`Program.cs` 改为注入。
- 效果：界面或 JSON 修改拦截规则后**无需重启** MCP。

### A3 审批增强
- `IApprovalService.RequestApprovalAsync` 改为接收 `ApprovalRequest`（服务器、命令、操作类型、目标路径、风险说明、超时秒数）。
- 实现改为自绘确认窗（不再用裸 MessageBox）：展示命令与影响面，按钮 **允许一次 / 本会话允许此命令 / 拒绝**，默认按钮为拒绝，超时自动拒绝。
- "本会话允许"为进程内白名单，且写入审计（标 `Approved`）。

### A4 文件传输策略落地（已有字段，直接启用）
- `FileTransferTools` 增加校验：`Enabled` 关闭则拒绝；上传源/下载目标须在 `AllowedLocalPaths`、上传目标/下载源须在 `AllowedRemotePaths`（路径规范化 + 前缀匹配）。
- 拒绝时返回 `status: "path_not_allowed"` / `disabled`，并记审计。
- 这部分是**低风险高收益**，优先实施。

### A5 自动发现路径白名单
- 新增 `security.discovery = { allowedSearchPaths: [...], requireApproval: true }`；`discover_topology` 的 `searchPaths` 仅允许白名单子集，越界即拒绝或触发审批。

### A6 审计与凭据
- audit.db 开启 `PRAGMA journal_mode=WAL; PRAGMA busy_timeout=3000;`，加索引 `(Timestamp)`、`(ServerId/DataSourceId)`。
- 新增 `security.audit = { storeSqlText: true, maskLiterals: false, retentionDays: 90 }`；可选对 SQL 字面量脱敏。
- 审计记录增加 `ClientName`（来自 MCP `clientInfo`）与 `SessionId`，便于区分来源。
- `ConfigService` 构造注入 `ISecretProtector`（默认可保持 DPAPI 实现），便于测试与替换。

---

## 2. 可视化配置（B）

### B1 应用(app)管理界面
- 新增 `Views/ApplicationEditWindow` + `ViewModels/ApplicationEditViewModel`（名称/类型/端口/host/描述/标签）。
- `DatasourceManageWindow` 增加"应用"分组或页签，支持增删改；应用即 `app:` 拓扑节点。

### B2 安全设置界面
- 新增 `SecuritySettingsWindow`：命令 `blocked/sensitive` 列表编辑、SQL 规则、文件传输（开关/允许路径/限额/审批开关）、SSH 主机密钥模式、发现路径白名单。
- 支持"从审计记录一键加入敏感/禁止规则"。

### B3 拓扑可视化
- 新增 `TopologyWindow`：Canvas 分层布局（服务器 → 应用 → 数据库），节点可点选看详情，边区分**人工声明**与**自动发现**（虚线/颜色）。
- 数据来自 `ITopologyService.GetGraphAsync`。

### B4 自动发现入口
- 在 `DatasourceManageWindow` / `TopologyWindow` 增加"自动发现拓扑"按钮，调用 `ITopologyService.DiscoverAsync` 并展示新增节点/边。

### B5 审计窗口
- 新增 `AuditWindow`：命令 / SQL 两个页签，支持按服务器/数据源筛选、按时间倒序、导出 CSV。

### B6 配置导入/导出/备份
- 导出支持"不含密钥"（导出前剥离加密后的密码字段）与"完全导出"；导入做结构校验与冲突提示。

### B7 服务器详情增强
- 显示实时连接状态、上次连接时间、所属隧道/被哪些数据源引用。

---

## 3. 运维能力（C）

### C1 日志文件
- `Microsoft.Extensions.Logging` 增加文件 sink：`%APPDATA%\LitSSH\logs\mcp-YYYYMMDD.log`，按天滚动、保留 N 天，stderr 保留（兼容 MCP 诊断）。

### C2 `health_check` 工具
- 自检：配置可读、`audit.db` 可写、`known_hosts` 可读；可选对指定服务器/数据源做连通性探测。返回结构化健康报告。

### C3 并发 / 速率限制
- 新增 `security.limits = { maxConcurrentPerTarget: 3, maxCallsPerMinutePerTarget: 30 }`；用 `SemaphoreSlim` 按 `serverId` / `datasourceId` 限流，超限返回 `status: "rate_limited"`。

### C4 取消支持
- 工具方法注入 MCP `RequestContext`/`CancellationToken` 并透传到 Core（Core 签名已支持 `ct`）。

### C5 审计轮转
- 按大小/时间归档旧记录（可配置 `retentionDays`），启动时或按天执行。

---

## 4. 质量与架构（D）

### D1 测试工程
- 新增 `tests/LitSSHmcp.Tests`（xUnit）：覆盖 `SqlFilterService`、`CommandFilterService`、`DpapiSecretProtector`、`AssetNode`、`TopologyService` 合并逻辑、`SshKnownHostsStore`、文件路径校验、配置迁移。
- 保留现有 stdio 冒烟脚本作为集成验收。

### D2 配置版本与迁移
- `AppConfig` 增加 `schemaVersion`（当前为 `1`）；新增 `ConfigMigrator`，`LoadConfigAsync` 载入后按版本迁移并回写。

### D3 统一错误模型
- 新增 `McpToolResult` / `ToolError` 帮助类型，统一 `{ success, status, error, ... }` 生成，替换各工具手写 JSON（渐进式，改一个删一处）。

### D4 用足 MCP 能力
- 工具注解：只读工具标 `ReadOnly`（如 `list_*`/`get_*`/`mysql_query`）、破坏性工具标 `Destructive`（`execute_command`/`mysql_execute`/`execute_with_sudo`），便于客户端提示用户。
- 可选：注册 resources（配置摘要/拓扑快照）、prompts（故障排查模板）。

### D5 结构优化
- `SshService`（600+ 行）按 命令 / 文件 / 探测 拆分；`ConfigService` 增加内存缓存与文件失效检测。

---

## 5. 实施分期

| 阶段 | 内容 | 说明 |
|------|------|------|
| **Phase 1** | A4、A2、A3(精简)、D2、C1、D1(起步)、D4 | 低风险高收益：先堵安全缺口、热更新、可观测、测试底座 |
| **Phase 2** | A1、A5、C3、C4、B1、B5 | 主机密钥、发现白名单、限流/取消、应用与审计界面 |
| **Phase 3** | A6、B2、B3、B4、B6、B7、C2、C5、D3、D5 | 审计治理、安全设置页、拓扑可视化、日志轮转、错误模型统一 |

每项独立可交付，阶段内按序进行，随时可调整。

## 6. 兼容性与风险控制

- 新配置字段均有默认值；`schemaVersion` 迁移保证旧文件可直接加载。
- 主机密钥默认 `tofu`（首连记录），不会因升级立即断联；`strict` 为显式可选。
- 工具返回结构不变，AI 侧无行为破坏。
- 每个 Phase 结束执行：`dotnet build LitSSHmcp.slnx`（0/0）+ 发布 McpServer + stdio 冒烟（tools/list 计数、关键工具调用、错误路径）。

## 7. 验收基线

- 构建 0 警告 0 错误；`publish` 成功。
- `tools/list` 返回工具数随新增（`health_check` 等）同步更新；文档 `docs/TOOLS.md` 同步。
- 安全相关新增校验需有对应的拒绝路径用例（单元测试 + 手工验证）。
