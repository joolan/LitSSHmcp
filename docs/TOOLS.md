# LitSSH MCP 工具参考

MCP 服务器当前注册 **51 个工具**，按用途分为 14 组。本文档说明每个工具的用途、参数、返回结构与选择路由；工具说明文本本身也内置了"当用户问…时使用"的意图提示（AI 客户端在 `tools/list` 时即可看到）。

> **⚠ 同步约定（唯一事实来源）**：本文件是 MCP 工具说明的**唯一事实来源**。以下三处必须与本文件**双向同步**，工具发生任何变动（新增/改名/删除、参数变化、描述与路由变化）时缺一不可：
> 1. **MCP 服务器端** — `src/LitSSHmcp.McpServer/Tools/*.cs` 中 `[McpServerTool]` / `[Description]` 注解、`Program.cs` 的 `WithTools<T>()` 注册（`get_usage_guide` 的内置工具清单由这些注解**反射生成**，无需手改）；
> 2. **本仓库文档** — `docs/TOOLS.md`（本文件，含 README/CHANGELOG 中的工具列表与数量）；
> 3. **桌面 App 端** — `src/LitSSHmcp.App/Views/McpToolsWindow`（菜单 **配置 → MCP工具说明**，本文件以 `EmbeddedResource` 嵌入后在窗口中展示，文案数量来自本文件）。
>
> 任一处遗漏都会导致 AI 客户端拿到的工具说明与实际能力不一致；一致性由 `tests/LitSSHmcp.McpServer.Tests` 自动校验（已注册工具 ↔ 本文件 ↔ `get_usage_guide` 清单）。
>
> **文档结构约定**：本文档的 `##` 标题即「工具分组」，格式 `## <中文分组名>（<分组键>）`，其中分组键与 `config.json` 的 `tools.enabledGroups`（及 App「工具分组设置」）严格一致；每个工具用 `### \`工具名\`` 表示；其它 `###` 子标题（不带反引号）只是章节内的说明小标题，不计入工具。
>
> **描述精简约定**：各工具 `[Description]` 只保留 1~2 句（做什么 + 关键互斥提示），长篇的"何时用 / 不要用"统一放在本文件的「意图 → 工具路由表」与各分组说明中，避免工具清单过长挤占 AI 上下文。

## 概览与接入

### 传输方式与客户端配置

**传输方式**：stdio（AI 客户端启动 MCP 服务器进程，通过标准输入输出通信）。服务器可执行文件由 `dotnet publish` 产出：`publish/LitSSHmcp.McpServer.exe`。

**通用配置模板**（Claude Desktop / Cursor / Windsurf / Cline / CodeBuddy 等）：

```json
{
  "mcpServers": {
    "litssh": {
      "command": "C:\\path\\to\\publish\\LitSSHmcp.McpServer.exe"
    }
  }
}
```

开发模式（未发布时）：

```json
{
  "mcpServers": {
    "litssh": {
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\LitSSHmcp\\src\\LitSSHmcp.McpServer", "--no-build"]
    }
  }
}
```

**Server Instructions**：服务器在 `initialize` 返回中携带会话级指令（按意图选组、ID 获取约定、写操作需桌面确认、不含密码等），AI 客户端会自动纳入上下文；完整的"意图→工具"路由仍由 `mcp_usage_guide` 提供。

**给 AI 的指令建议**（让工具用得更准）：
- 直接把本文档的"意图 → 工具路由表"贴进系统提示词，或告诉模型"先调用 `mcp_usage_guide` 再决定用哪个工具"；
- 强调 ID 约定：服务器 `serverId` 来自 `ssh_list_servers`，数据库 `datasourceId` 来自 `datasource_list`；
- 强调易混点：`ssh_test_connection`=SSH 连通性，`datasource_test_connection`=数据库/Redis 连通性；`ssh_execute_command`=Shell，`mysql_query/mysql_execute`=SQL，`redis_read/redis_execute`=Redis 命令（写操作需桌面审批）；
- 敏感命令/SQL 会弹确认（桌面 + CLI 双通道），超时（默认 45s）返回 `status: "approval_timeout"`、拒绝返回 `"rejected"`、通道不可用返回 `"approval_unavailable"`；都不要重试轰炸。

**运行前提**：配置文件 `%APPDATA%\LitSSH\config.json`（本 App 中维护的服务器/数据源/安全策略）；命令与 SQL 审计写入 `%APPDATA%\LitSSH\audit.db`；日志在 `%APPDATA%\LitSSH\logs`。

### 工具分组（按部署裁剪）

`config.json` 的 `tools.enabledGroups` 可只暴露部分工具，降低 AI 上下文占用与误选。留空 / 不写 = 全部启用；写 `["all"]` = 全部；写 `["none"]` = 全部停用（AI 将看不到任何工具）；分组名大小写不敏感，未知分组会被忽略并在启动日志告警。桌面 App 菜单 **配置 → 工具分组设置** 可图形化勾选。

```json
{ "tools": { "enabledGroups": ["ssh", "command", "datasource", "mysql", "guide"] } }
```

| 分组键 | 中文名 | 包含的工具 |
|--------|--------|-----------|
| `ssh` | SSH 服务器 | `ssh_list_servers`、`ssh_get_server_status`、`ssh_test_connection`、`ssh_snapshot_get`、`ssh_snapshot_refresh` |
| `command` | 命令执行 | `ssh_execute_command`、`ssh_get_command_history`、`ssh_execute_sudo`、`ssh_get_sudo_status` |
| `fileTransfer` | 文件传输 | `ssh_upload_file`、`ssh_download_file`、`ssh_list_files` |
| `datasource` | 数据源 | `datasource_list`、`datasource_test_connection`、`datasource_get_sql_history` |
| `mysql` | MySQL 执行 | `mysql_query`、`mysql_execute`、`mysql_explain`、`mysql_diagnostics` |
| `postgres` | PostgreSQL 执行 | `postgres_query`、`postgres_execute`、`postgres_explain`、`postgres_diagnostics` |
| `redis` | Redis | `redis_read`、`redis_execute`、`redis_diagnostics` |
| `docker` | Docker 容器 | `docker_ps`、`docker_logs`、`docker_inspect`、`docker_stats`、`docker_images`、`docker_restart`、`docker_exec` |
| `service` | systemd 服务 | `service_status`、`service_list`、`service_restart`、`service_logs` |
| `log` | 日志文件 | `log_tail`、`log_grep`、`log_find` |
| `java` | JVM 诊断 | `java_processes`、`java_threads`、`java_heap`、`java_info` |
| `topology` | 拓扑 | `topology_get_overview`、`topology_get_dependencies`、`topology_discover` |
| `app` | 应用体检 | `app_health_snapshot` |
| `guide` | 指南/自检 | `mcp_usage_guide`、`mcp_self_check`、`mcp_list_sessions` |

> 注意：只启用 `mysql`/`redis` 而不启用 `datasource` 时，AI 将没有 `datasource_list` 来获取 `datasourceId`（启动日志会给出告警）。

### 工具命名约定

所有工具名遵循 **`<域>_<动作>[_<对象>]`** 的小写 snake_case。域前缀让工具在 `tools/list` 中按域聚类，便于 AI 路由与人工查找：

| 域 | 前缀 | 覆盖范围 |
|----|------|---------|
| SSH 服务器 | `ssh_` | 服务器列表 / 状态 / 连通性 / 整机快照 / 命令执行（含 sudo）/ 文件传输 / 命令历史 |
| 数据源 | `datasource_` | 数据源列表 / 连通性 / SQL 与 Redis 审计历史 |
| MySQL | `mysql_` | 只读查询 / 写执行 / 执行计划 / 整体诊断 |
| PostgreSQL | `postgres_` | 只读查询 / 写执行 / 执行计划 / 整体诊断 |
| Redis | `redis_` | 只读命令 / 写命令 / 整体诊断 |
| Docker | `docker_` | 容器列表 / 日志 / 详情 / 资源 / 镜像 / 重启 / exec |
| systemd | `service_` | 服务状态 / 列表 / 重启 / journalctl 日志 |
| 日志文件 | `log_` | 读取尾部 / 关键字检索（受白名单约束） |
| JVM | `java_` | 进程列表 / 线程栈 / 堆与GC / JVM信息 |
| 拓扑 | `topology_` | 拓扑总览 / 资产依赖 / 自动发现 |
| 应用 | `app_` | 跨服务器+数据源的一键体检快照 |
| MCP 自身 | `mcp_` | 使用指南 / 自检 |

- 规范动词：`list` / `get` / `test` / `execute` / `read` / `query` / `explain` / `upload` / `download` / `discover` / `diagnostics`。
- **禁止无域通用名**（如 `health_check`、`test_connection`），一律带域前缀。
- 新增工具必须遵循本约定，并同步本文件顶部列出的三处。

### 意图 → 工具路由表

选工具的第一原则：**按用户意图路由，并注意 SSH 侧与数据库侧的易混工具**。

| 用户问法 | 使用工具 | 不要用 |
|---------|---------|--------|
| 有哪些服务器 / SSH服务器列表 / 连了哪些机器 | `ssh_list_servers` | ~~datasource_list~~（那是数据库） |
| 某台服务器的状态 | `ssh_get_server_status` | |
| SSH能不能连上 / 测试服务器连接 | `ssh_test_connection` | ~~datasource_test_connection~~（那是数据库） |
| 服务器整体态势 / 资源占用(CPU/内存/磁盘/负载) / 端口进程服务 / Docker 容器 / 证书到期 / systemd健康 / 安全巡检 | `ssh_snapshot_get`（先查已存快照）; 需最新数据用 `ssh_snapshot_refresh` | 不要为看态势反复拼 `ssh_execute_command` |
| 在服务器上执行命令 | `ssh_execute_command` | |
| 权限不足 / 需要root执行 | `ssh_execute_sudo` | 不要等失败再提权，主动判断 |
| 服务器上有哪些文件 / 看目录 | `ssh_list_files` | |
| 这台机器能不能sudo / 有没有配置提权 | `ssh_get_sudo_status` | |
| 上传/下载文件 | `ssh_upload_file` / `ssh_download_file` | |
| **有哪些MySQL / 数据库列表 / 数据库配置信息** | `datasource_list` | ~~ssh_list_servers~~（那是SSH） |
| **数据库能不能连上 / 测试数据库连接 / 数据库连不上** | `datasource_test_connection` | ~~ssh_test_connection~~（那是SSH） |
| MySQL服务正不正常 / 数据库为什么慢 / 连接数暴涨 | `mysql_diagnostics`（先做整体诊断） | |
| 查数据 / 看表结构 / 建表语句 / 查processlist | `mysql_query` | |
| 改数据 / 建表 / 加字段 / 清数据 | `mysql_execute` | |
| 这条SQL为什么慢 / 执行计划 | `mysql_explain` | |
| PostgreSQL正不正常 / PG为什么慢 / 连接数暴涨 | `postgres_diagnostics`（先做整体诊断） | |
| PG查数据 / 看表结构 | `postgres_query` | |
| PG改数据 / 建表 / 加字段 | `postgres_execute` | |
| PG这条SQL为什么慢 / 执行计划 | `postgres_explain` | |
| 谁执行了什么SQL / SQL审计记录 | `datasource_get_sql_history` | |
| Redis正不正常 / 缓存为什么慢 / 内存涨 / 命中率低 / 卡 | `redis_diagnostics`（先做整体诊断） | |
| 查缓存 / 读key / 看集合内容 / 慢日志 | `redis_read` | ~~redis_execute~~（那是写） |
| 写缓存 / 删key / 设过期时间 / 改Redis配置 / 踢客户端 | `redis_execute`（弹桌面确认） | ~~redis_read~~（只读，非白名单会拒绝） |
| 清库/关服/换主从/阻塞类Redis命令 | 任何工具都返回 `blocked`（不会执行） | 不要改写绕过 |
| 有哪些容器 / 容器状态 / 容器端口 | `docker_ps` | |
| 看某容器日志 / 容器起不来 | `docker_logs` | |
| 容器配置 / 环境变量 / 挂载 / 网络 | `docker_inspect` | |
| 容器占CPU/内存 | `docker_stats` | |
| 镜像列表 / 版本 / 磁盘占用 | `docker_images` | |
| 重启容器 / 容器内执行命令 | `docker_restart` / `docker_exec`（需人工确认） | |
| 服务状态 / 服务起没起 / 服务退出码 | `service_status` | |
| 有哪些服务 / 找服务名 | `service_list` | |
| 重启服务 | `service_restart`（需人工确认） | ~~docker_restart~~（那是容器） |
| 服务日志 / journalctl | `service_logs` | ~~docker_logs~~（那是容器） |
| 看日志文件尾部 | `log_tail` | ~~ssh_execute_command~~（不要自己拼 tail） |
| 日志里搜错误/关键字 | `log_grep` | |
| 不知道日志路径 / 找最近日志文件 | `log_find`（先发现再读） | |
| 有哪些Java进程 / 应用pid | `java_processes`（可传 appId 过滤） | |
| 线程栈 / 死锁 / CPU飙高 / 线程池满 | `java_threads`（可传 appId 自动解析 pid） | |
| 堆内存 / 频繁GC / OOM | `java_heap`（可传 appId） | |
| JVM版本 / 运行时长 | `java_info`（可传 appId） | |
| 某应用整体体检(跨服务器+数据库) | `app_health_snapshot` | 先看它再下钻 |
| 系统架构 / 拓扑 / 应用部署在哪 / 依赖关系 | `topology_get_overview` | |
| 某资产的上下游依赖 | `topology_get_dependencies` | |
| 拓扑缺失或过期 | `topology_discover` | |
| 命令执行历史 | `ssh_get_command_history` | |
| 不确定用哪个 | `mcp_usage_guide`（返回内置使用指南） | |
| MCP是否正常 / 工具用不了 / 自检 | `mcp_self_check` | |
| 审计按客户端/会话区分 / 有哪些会话 | `mcp_list_sessions` → 再用 `*_history` 的 `sessionId` 过滤 | |

### ID 获取约定

- 数据库类工具的 `datasourceId` → 用 `datasource_list` 获取（也接受数据源名称）；
- SSH 类工具的 `serverId` → 用 `ssh_list_servers` 获取（也接受服务器名称/主机名）；
- 拓扑工具的 `assetId` → `ssh:xx` / `ds:xx` / `app:xx` 前缀格式，也接受纯 ID 或名称。

> **多服务器防呆**：`serverId`/`datasourceId` 支持 ID、名称、主机名，但**若名称或主机名匹配到多台目标，工具会返回 `server_ambiguous` / `datasource_ambiguous` 并拒绝执行**（精确 ID 优先），绝不静默猜测——避免在错误的机器上执行命令。各工具返回都回显 `serverId`/`serverName`/`host`（数据源为 `datasourceId`/`name`/`host`），人工审批弹窗/CLI 待决文件显示 `名称(用户@主机:端口)`，便于核对真实目标。

### 通用约定

- 所有返回均为 JSON；**除 `mcp_usage_guide`（使用指南文档）外，全部工具均提供结构化输出**（MCP `structuredContent` + `outputSchema`），并同时保留等价的 text(JSON)（向后兼容）。注意：`ssh_list_servers`/`datasource_list`/`*_history` 等由裸数组改为 `{ success, count, ... }` 包裹；`ssh_execute_command` 等命令类返回 `output`/`error`（非 stdout/stderr）；字段统一 camelCase；
- 成功/失败统一以 `success` 字段为主，配合 `status` 细分错误类型。常见 `status`：`blocked`（命中禁止规则，**不要重试**）、`rejected`（用户拒绝）、`approval_timeout`（审批超时）、`approval_unavailable`（审批通道不可用，如无桌面且未启用 CLI）、`path_not_allowed`、`file_transfer_disabled`、`file_not_found`、`file_too_large`、`*_not_found`（ID 不存在，错误里会回显可用 ID）、`readonly_statement`（只读语句用错写工具）、`not_readonly_statement`（写语句用错只读工具）、`not_readonly`（Redis 非只读命令用错 `redis_read`）、`sudo_not_configured`、`auth_failed`、`host_key_mismatch`（主机密钥变化，可能是安全事件，先人工核对指纹）、`timeout`、`rate_limited`（被限流，应退避重试）、`connection_error`；
- `serverId` / `datasourceId` 支持 **ID / 名称 / 主机名**（忽略大小写）三种写法；传错时错误信息回显可用 ID；
- 命令输出超过 2 万字符会被截断并置 `truncated: true`、`outputChars` 记录原始长度；审计库中单条结果截断到 4000 字符；
- 敏感操作（敏感命令、敏感 SQL、Redis 写命令、文件传输）需人工确认，默认同时启用**桌面弹窗 + CLI 带外审批**两条通道（`security.approval.channels`，默认 `["desktop","cli"]`，超时默认 45 秒）；AI 侧表现为 `status: "rejected"` / `"approval_timeout"` / `"approval_unavailable"`；第一期 Redis 写命令**一律**确认（不按敏感度分级）；**审批模式**（`security.approval.mode`）：`manual`（默认，所有触发审批的操作都需人工确认）、`auto-approve`（危险：所有触发审批的操作自动放行）、`auto-reject`（触发审批时直接拒绝），可在桌面 App「安全设置 → 审批模式」切换；三种模式都**不影响**被命令过滤器硬拒绝（`blocked`）的操作；
- 每个工具带 MCP 注解 `ReadOnly` / `Destructive` / `Idempotent` / `OpenWorld`，只读工具与破坏性工具易于在客户端区分；
- 工具名遵循「工具命名约定」（`<域>_<动作>[_<对象>]`）；
- 任何工具的入参、出参都**不包含密码**；
- 所有操作都会写入本机审计库 `%APPDATA%\LitSSH\audit.db`（既是**操作日志**也是**审计日志**）：命令/SQL 执行、审批与拦截、只读探测（连接测试/列目录）、列表元数据（列服务器/数据源/拓扑）、文件传输。每条记录带 `sessionId`（哪个客户端/哪次连接）、`tool`（哪个 MCP 工具产生）、`category`（`exec`/`gate`/`probe`/`meta`/`transfer`）与 `decision`（Gate 类的审批决策：`manual-approved`/`manual-rejected`/`auto-approve`/`auto-reject`/`timeout`/`unavailable`/`blocked`），这些字段都**参与哈希链防篡改**（改动会导致完整性校验失败）；当前会话 ID 可用 `mcp_self_check` 查看，历史工具支持 `sessionId` / `tool` / `category` 过滤；MCP 运行日志在 `%APPDATA%\LitSSH\logs`。

## SSH 服务器组（ssh）

### `ssh_list_servers`
- **参数**：无
- **返回**：`{ success, count, servers: [...] }`，每项含 `id/name/host/port/username/authType/description/tags/lastConnectedAt`（**无密码**）
- **说明**：**已禁用的服务器不会出现在本列表中**。服务器一旦禁用：① 不出现在 `ssh_list_servers`；② 任何按服务器标识解析的工具（`ssh_*` / `docker_*` / `service_*` / `log_*` / `java_*` / 应用与文件等）都返回 `server_disabled` 并拒绝执行；③ 资产拓扑中该服务器不可建链/连接；④ 拓扑自动发现跳过它。需在桌面 App 的「服务器编辑」里取消勾选“禁用”并保存后恢复。

### `ssh_get_server_status`
- **参数**：`serverId` — 服务器标识（ID/名称/主机名，可用 `ssh_list_servers` 列出）
- **返回**：`{ success, id, name, host, status, errorKind, durationMs }`（`status` 为 `connected`，失败时取 `auth_failed`/`host_key_mismatch`/`timeout`/`connection_error`；服务器被禁用时为 `server_disabled`）

### `ssh_test_connection`
- **参数**：`serverId` — 服务器标识（ID/名称/主机名）
- **返回**：`{ success, status, errorKind, serverId, name, durationMs, error }`
- **说明**：只测 SSH 连通性并区分失败根因（`auth_failed`/`host_key_mismatch`/`timeout`/`connection_error`）；测数据库请用 `datasource_test_connection`

### `ssh_snapshot_get`
- **参数**：`serverId`（服务器标识）、`snapshotId`（可选，取某一份历史快照；不传取该服务器**最新一份**）
- **返回**：`{ success, status, hint, serverId, serverName, host, snapshotId, state, createdAt, completedAt, durationMs, escalation, collectorVersion, data, events, recent }`
  - `data`：`{ collectorVersion, elevated, sections }`，`sections` 按采集维度分组：`resource`（资源态势：负载/内存/磁盘/CPU/OS/内核/主机名/IP）、`portmap`（端口↔进程↔用户↔服务三元组 + **程序路径 `exe`** + **完整启动命令行 `cmdline`**，含 TCP/UDP、双栈、Unix socket）、`docker`（守护进程概览：版本/容器与镜像数/存储驱动 + 容器清单 名称/镜像/状态/端口 + 运行容器资源 CPU/内存/网络/块IO/PIDs；未装 docker 为 `skipped`）、`nginx_tls`（**完整有效配置 `effectiveConfig`（`nginx -T` 展开 include）+ 域名列表 `domains`（每个域名是否 `ssl`、监听端口、关联证书到期/SAN）+ 站点/证书明细**）、`systemd`（单元健康聚合/失败清单）、`security`（安全巡检：`ssh` 有效配置、`firewall`（ufw/firewalld/iptables）、`fail2ban`、`mysql` 匿名账户/远程 root/**可远程登录的高权账户（`SHOW GRANTS` 判定）**、`exposedHighRiskPorts`、系统空口令账户、sudoers NOPASSWD，并汇总为 `findings[]`（severity/id/title/detail/evidence）与 `summary` 计数。**MySQL 账户核查优先用"与该服务器匹配的已配置 MySQL 数据源凭据"查询 `mysql.user`**（匹配规则：数据源 Host 为回环且其 SSH 隧道服务器 == 本服务器，或数据源 Host == 本服务器 host/本机 IP）；该账号需拥有 `mysql.*` 的 SELECT 权限（不要求是 root），否则 `mysql.checked=false` 并在 `reason` 说明（不会误判为安全）；无匹配数据源时回退到免密 best-effort）；每个 section 含 `status`（`ok`/`degraded`/`skipped`/`failed`）、`durationMs`、`error`、`note`、`data`
  - `events`：该份快照的采集事件流水（`started`/`collector_started`/`collector_completed`/`collector_failed`/`collector_skipped`/`completed`/`failed`，含每步耗时与失败原因）
  - `recent`：该服务器最近 10 份快照的轻量摘要（`id`/`state`/`createdAt`/`durationMs`/`error`），用于一眼看历史与后续趋势
- **说明**：快照持久化在本机独立库 `%APPDATA%\LitSSH\snapshots.db`（与审计库分离，便于独立备份/清理）。**默认只读本地已存快照，不连服务器**；从未生成过返回 `status=snapshot_not_found` 并提示用 `ssh_snapshot_refresh` 生成首份。**降级采集**（section `status=degraded` 并给 `note`）：未提权或缺权限时 `portmap` 的属主/服务归属、`security` 的系统空口令/MySQL 账户可能缺失；`docker`/`nginx_tls`/`systemd` 环境不具备时对应 section `status=skipped`。**排查服务器问题优先用本工具**（一次拿全整机态势，比逐条拼命令全面稳定）。

### `ssh_snapshot_refresh`
- **参数**：`serverId` — 服务器标识
- **返回**：同 `ssh_snapshot_get` 的结构；成功 `status=succeeded`，失败 `status=failed`（并保留已采集到的部分 section 与失败事件，便于排障）
- **说明**：**重新采集**整机快照（与 `ssh_snapshot_get` 同一套 data 结构）。**耗时较长**（典型 10~30 秒，弱网更久），工具描述已明确提示。**单飞限流**：同一服务器同时只允许一个快照在采集中，重复调用立即返回 `status=snapshot_in_progress`（含进行中的 `snapshotId`），不会排队或重复采集——此时应改用 `ssh_snapshot_get` 稍后查询。**失败也落库**：连接失败/超时/取消都会把该份快照记为 `failed` 并写入 `error` 与事件；`security` 兼容性上，采集命令均为**内置固定只读命令**，但仍受命令过滤器 `Blocked` 规则约束。
- **提权**：由 `config.json` 的 `snapshot.useSudo`（默认 `true`）与该服务器 `SudoType` 共同决定；开启且已配置提权时自动以 sudo/su 执行（`portmap` 可见 root 进程属主与 systemd 服务名、`nginx_tls` 可读 root-only 证书目录），**不逐次弹审批**；未配置/关闭时自动降级，不报错。设置 `snapshot.useSudo=false` 可完全禁止快照提权。
- **保留**：每台服务器保留最近 `snapshot.retentionPerServer` 份（默认 30，`0`=不限），超出时按时间裁剪并级联清理其事件。

## 命令执行组（command）

### `ssh_execute_command`
- **参数**：`serverId`（服务器标识）、`command`（Shell命令）、`timeoutSeconds`（可选，命令超时秒数，1-3600，默认 60；超时返回 `status=timeout`）
- **返回**：`{ success, status, reason, error, command, output, truncated, outputChars, exitCode, durationMs }`（`output` 为命令输出，超 2 万字符截断并置 `truncated: true`；被拒绝/禁止时 `status` 为 `rejected`/`blocked`/`approval_timeout`/`approval_unavailable`）
- **防挂起**：会持续输出/需交互的命令（`tail -f`、`docker logs -f`、`journalctl -f`、`vi/vim/less/top/watch`、普通通道的 `sudo/su`、`ping` 不带 `-c`、`nc/telnet`、`docker exec -it`、`docker attach` 等）**不会执行**，直接返回 `status=blocking_command` 并给出替代写法（如 `tail -n`/`--tail`/`--no-pager`/用 `ssh_execute_sudo`）。启动常驻进程请用 `nohup ... &`/`setsid`/`systemctl`/`docker -d`，不要前台跑。
- **安全链路**：挂起/交互拦截 → 黑名单直接拒绝 → 敏感命令人工确认 → 执行 → 写审计（sudo 提权命令用 `ssh_execute_sudo`）

### `ssh_execute_sudo`
- **参数**：`serverId`、`command`
- **返回**：同 `ssh_execute_command`，并额外带 `escalation` —— 本次实际提权机制：`direct`（未提权/已是目标用户）、`sudo`、`su`、`auto:sudo`、`auto:su`、`auto:failed`（用于排障与向用户说明“到底用了 sudo 还是 su”）；未配置提权时返回 `status: "sudo_not_configured"`
- **说明**：判断需要 root 权限时**直接用本工具**（无需预检），不要先 `ssh_execute_command` 失败再提权；总是需要人工确认。具体走 `sudo` 还是 `su` 由服务器配置（`SudoType`）决定，工具会自动选择（含 `Auto` 先 sudo 失败回退 su），客户端无需也不应预先判断。
- **安全**：返回给客户端的 `output`/`error` 一律把服务器口令（SSH 密码 / 密钥口令 / 提权密码）脱敏为 `******`；任何路径（含异常）都不会把密码暴露给 AI。

### `ssh_get_sudo_status`
- **参数**：`serverId`
- **返回**：`{ success, serverId, serverName, sudoType, sudoUsername, isConfigured, description }`
- **说明**：仅在需要向用户解释"为什么不能提权"时使用

### `ssh_get_command_history`
- **参数**：`serverId`（可选，不传查全部）、`limit`（默认 50，上限 200）、`offset`（翻页偏移，默认 0）、`sessionId`（可选，只查某个 MCP 会话）、`tool`（可选，只查某个 MCP 工具产生的记录，如 `docker_logs`）、`category`（可选，事件类型：`exec`=执行 / `gate`=审批拦截 / `probe`=只读探测 / `meta`=列表元数据 / `transfer`=文件传输）
- **返回**：`{ success, status, error, count, hasMore, records: [...] }`（每条记录含 `sessionId`/`tool`/`category`/`decision`；`result` 已截断到 4000 字符；`hasMore: true` 表示还有更早记录，配合 `offset` 翻页）

**命令类错误约定**：`{ success: false, error, status, errorKind }`，`status` 可能值：`blocked`、`blocking_command`、`rejected`、`approval_timeout`、`approval_unavailable`、`server_not_found`、`server_disabled`、`sudo_not_configured`、`file_not_found`、`auth_failed`、`host_key_mismatch`、`timeout`、`rate_limited`、`connection_error`、`failed`。

## 文件传输组（fileTransfer）

上传/下载受 `fileTransfer.enabled` 开关与 `allowedLocalPaths` / `allowedRemotePaths` 白名单约束（越界返回 `path_not_allowed`，禁用返回 `file_transfer_disabled`），路径会做规范化并拦截 `..` 穿越。上传/下载过程中会通过 MCP `notifications/progress` 推送进度（字节数/百分比）。

### `ssh_upload_file`
- **参数**：`serverId`、`localPath`（本地路径，须在 `allowedLocalPaths` 内）、`remotePath`（远程路径，须在 `allowedRemotePaths` 内）
- **返回**：`{ success, message, bytesTransferred, durationMs, ... }`；越界返回 `path_not_allowed` 并附允许路径；超过 `fileTransfer.maxFileSizeBytes` 返回 `file_too_large`

### `ssh_download_file`
- **参数**：`serverId`、`remotePath`（须在 `allowedRemotePaths` 内）、`localPath`（须在 `allowedLocalPaths` 内）
- **返回**：`{ success, message, bytesTransferred, durationMs, ... }`；同样受审批与白名单约束

### `ssh_list_files`
- **参数**：`serverId`、`remotePath`（远程目录）
- **返回**：`{ success, status, error, path, count, truncated, files: [{ name, fullName, size, lastModified, isDirectory, isSymbolicLink }] }`
- **说明**：`size` 为可读字符串（如 `"1.5 MB"`）；路径不存在/无权限会返回失败状态而非空列表；条目过多时 `truncated: true`

## 数据源组（datasource）

### `datasource_list`
- **参数**：无
- **返回**：`{ success, count, dataSources: [...] }`，每项含 `id/name/type/host/port/username/defaultDatabase/accessMode/tunnelServerId/tunnelServer/description/tags/accessibleFromSshServers/connectedApplications`
- **说明**：**密码永不出现在返回中**；`type` 为 `mysql` / `postgres` / `redis`（按类型使用对应组的工具）；`host/port/username` 可直接用于与应用日志中的连接串比对

### `datasource_test_connection`
- **参数**：`datasourceId` — 数据源标识（ID/名称，可用 `datasource_list` 列出）
- **返回**：`{ success, status, datasourceId, name, host, port, accessMode, viaTunnelServer, version, durationMs, error }` —— 自动选择直连或建 SSH 隧道

### `datasource_get_sql_history`
- **参数**：`datasourceId`（可选，ID/名称）、`limit`（默认 50，上限 200）、`offset`（翻页偏移，默认 0）、`sessionId`（可选，只查某个 MCP 会话）、`tool`（可选，只查某个 MCP 工具产生的记录，如 `mysql_query`）、`category`（可选，事件类型：`exec`=查询/写入执行 / `gate`=写审批拦截 / `probe`=测试/诊断/EXPLAIN）
- **返回**：`{ success, status, error, count, hasMore, records: [...] }`（含被拒绝/被驳回的操作；每条记录含 `sessionId`/`tool`/`category`/`decision`；`result` 已截断到 4000 字符）
- **说明**：Redis 命令审计也写在同一张表，`operation` 为 `Query`/`Execute`/`Diagnostics`，SQL 列是命令原文；写操作的审批决策记在 `decision`（`manual-approved`/`auto-approve`/`manual-rejected`/`blocked`）

## MySQL 执行组（mysql）

所有 MySQL 工具都要求先有数据源配置（WPF 界面或 `config.json` 录入），且入参只有 `datasourceId`，**不含账号密码**。

### `mysql_query`（只读）
- **参数**：`datasourceId`（ID/名称）、`sql`（只读语句：SELECT/SHOW/EXPLAIN/DESC/WITH）、`maxRows`（默认 100，上限 1000）
- **返回**：`{ success, status, datasourceId, name, columns, rows, rowCount, truncated, durationMs, error }`
- **限制**：单条语句；非只读语句返回 `status: "blocked"`；**数据修改型 CTE（`WITH ... DELETE/UPDATE/INSERT`）与 `EXPLAIN ANALYZE <DML>`** 会被识别为写操作返回 `status: "not_readonly_statement"`，均提示改用 `mysql_execute`

### `mysql_execute`（写操作）
- **参数**：`datasourceId`（ID/名称）、`sql`（单条 INSERT/UPDATE/DELETE/DDL）
- **返回**：`{ success, status, datasourceId, name, rowsAffected, durationMs, error }`
- **安全链路**：危险语句（DROP/无WHERE的DELETE等）直接拒绝 → 敏感语句人工确认 → 执行 → 全部写审计
- **提示**：只读语句传入会返回 `status: "readonly_statement"`，提示改用 `mysql_query`；数据源只读时返回 `status: "readonly_datasource"`

### `mysql_explain`
- **参数**：`datasourceId`（ID/名称）、`sql`（SELECT语句）
- **返回**：`{ success, status, columns, rows, durationMs, error }`（EXPLAIN 计划表）

### `mysql_diagnostics`
- **参数**：`datasourceId`（ID/名称）
- **返回**：`{ success, status, datasourceId, name, summary, data, durationMs, error }`（`data` 内含连接数/慢查询/锁等待/复制/进程列表等）
- **场景**：用户问"MySQL正不正常/为什么慢/卡住/连接数暴涨"时**优先用它**做整体诊断，再按需下钻

**MySQL 错误约定**：`{ success: false, status, error }`，`status` 可能值：`datasource_not_found`、`unsupported_type`、`blocked`、`readonly_statement`、`not_readonly_statement`、`readonly_datasource`、`rejected`、`approval_timeout`、`approval_unavailable`、`execute_error`、`query_error`。

## PostgreSQL 执行组（postgres）

所有 PostgreSQL 工具都要求数据源 `type = postgres`（桌面 App"添加数据源"→类型选 PostgreSQL，端口默认 5432），入参只有 `datasourceId`，**不含账号密码**。连接自动选择直连或经 SSH 隧道（复用同一套隧道/限流/密钥校验链路）。仅读/写/敏感过滤与 MySQL 共用同一套 SQL 安全策略。

### `postgres_query`（只读）
- **参数**：`datasourceId`（ID/名称）、`sql`（只读语句：SELECT/SHOW/EXPLAIN/WITH）、`maxRows`（默认 100，上限 1000）
- **返回**：`{ success, status, datasourceId, name, columns, rows, rowCount, truncated, durationMs, error }`
- **限制**：单条语句；非只读语句返回 `status: "blocked"`；数据修改型 CTE 与 `EXPLAIN ANALYZE <DML>` 返回 `status: "not_readonly_statement"`，提示改用 `postgres_execute`

### `postgres_execute`（写操作）
- **参数**：`datasourceId`（ID/名称）、`sql`（单条 INSERT/UPDATE/DELETE/DDL）
- **返回**：`{ success, status, datasourceId, name, rowsAffected, durationMs, error }`
- **安全链路**：危险语句（DROP/无WHERE的DELETE等）直接拒绝 → 敏感语句人工确认（或按数据源 `WriteApproval=AutoApprove` 放行）→ 执行 → 全部写审计
- **提示**：只读语句传入会返回 `status: "readonly_statement"`；数据源只读时返回 `status: "readonly_datasource"`

### `postgres_explain`
- **参数**：`datasourceId`（ID/名称）、`sql`（SELECT语句）
- **返回**：`{ success, status, columns, rows, durationMs, error }`（EXPLAIN 计划）

### `postgres_diagnostics`
- **参数**：`datasourceId`（ID/名称）
- **返回**：`{ success, status, summary, data, ... }` —— 含版本/当前库、连接数(当前/活动/上限)、活动会话按状态、最耗时查询、等待锁、复制、死锁、缓存命中率、数据库大小
- **场景**：用户问"PG 正不正常 / 为什么慢 / 卡 / 连接数暴涨"时**优先用它**做整体诊断

**PostgreSQL 错误约定**：`{ success: false, status, error }`，`status` 可能值：`datasource_not_found`、`unsupported_type`、`blocked`、`readonly_statement`、`not_readonly_statement`、`readonly_datasource`、`rejected`、`approval_timeout`、`approval_unavailable`、`execute_error`、`query_error`。

## Redis 组（redis）

所有 Redis 工具都要求数据源 `type = redis`（桌面 App"添加数据源"→类型选 Redis，端口默认 6379），入参只有 `datasourceId`，**不含密码**。连接自动选择直连或经 SSH 隧道（复用 MySQL 的隧道/限流/密钥校验链路），Redis 6+ ACL 用户名填在"用户名"字段，无 ACL 留空（只发 AUTH 密码，密码不会落到跳板机）。命令按内置策略 `RedisCommandPolicy` 分三档：只读白名单 / 写（需审批）/ 禁止（直接拒绝）。

### `redis_read`（只读）
- **参数**：`datasourceId`、`command`（只读命令，如 `GET key` / `HGETALL user:1` / `SLOWLOG GET 10`）、`maxItems`（数组返回最大元素数，默认 200，上限 1000）
- **返回**：`{ success, datasourceId, name, command, result, truncated, durationMs, accessMode, viaTunnelServer }`；`result` 为**命令结果的 JSON 文本**（标量如 `"hello"`/`42`，数组为 JSON 数组），超长字符串/超大数组会截断（`truncated: true`）
- **限制**：不在只读白名单内的命令返回 `status: "not_readonly"` 并提示改用 `redis_execute`；禁止类命令返回 `status: "blocked"`
- **审计**：只读命令同样写 SQL 审计（`operation = Query`，SQL 列记录命令原文）

### `redis_execute`（写/管理）
- **参数**：`datasourceId`、`command`（如 `SET session:1 'abc' EX 60` / `DEL k` / `HSET user:1 name tom` / `CONFIG SET maxmemory 1gb`；含空格的值用引号包裹）
- **返回**：`{ success, datasourceId, name, command, result, durationMs }`（`result` 为命令结果的 JSON 文本）
- **安全链路**：禁止类命令（`SHUTDOWN`/`FLUSHALL`/`FLUSHDB`/`DEBUG`/`SWAPDB`/`REPLICAOF`/`SUBSCRIBE`/`BLPOP`/`MODULE LOAD` 等）直接拒绝 → **其余写操作一律弹桌面确认**（第一期不做敏感度分级，全部要求审批）→ 执行 → 写审计
- **提示**：只读命令传入会返回 `status: "readonly_statement"`，提示改用 `redis_read`

### `redis_diagnostics`
- **参数**：`datasourceId`
- **返回**：`{ success, datasourceId, name, summary, data, durationMs, error }` —— 内含 `INFO`（server/memory/clients/stats/keyspace）、`DBSIZE`、`CLIENT LIST`、`SLOWLOG GET`、`CONFIG` 关键项与 keyspace 摘要
- **场景**：用户问"Redis 正不正常 / 缓存为什么慢 / 内存涨 / 命中率低 / 有没有卡"时**优先用它**做整体诊断，再按需下钻

**Redis 错误约定**：`{ success: false, status, error }`，`status` 可能值：`datasource_not_found`、`unsupported_type`、`blocked`、`not_readonly`、`readonly_statement`、`rejected`、`approval_timeout`、`approval_unavailable`、`invalid_command`、`redis_error`（Redis 返回的错误）、`connection_error`。

## Docker 容器组（docker）

容器工具在目标服务器上执行 `docker` 命令，统一走命令过滤 + 审计；`docker_restart` / `docker_exec` 属敏感操作，需人工确认。容器名会做字符校验（`[A-Za-z0-9][A-Za-z0-9_.-]*`）并单引号转义，避免注入。

### `docker_ps`
- **参数**：`serverId`、`all`（默认 false=仅运行中，true=含已停止）
- **返回**：`{ success, status, error, count, truncated, containers: [{ id, names, image, status, state, ports }] }`
- **场景**："有哪些容器 / 容器状态 / 端口映射"时首选

### `docker_logs`
- **参数**：`serverId`、`container`、`tail`（默认 200，上限 5000）、`since`（可选，如 `10m` / `2h` / ISO 时间）
- **返回**：`{ success, status, error, output, truncated, outputChars, exitCode, durationMs }`
- **场景**：容器启动失败 / 报错时首选

### `docker_inspect`
- **参数**：`serverId`、`container`
- **返回**：同上的文本结果（body 为 `docker inspect` 的 JSON）

### `docker_stats`
- **参数**：`serverId`
- **返回**：同上；每行 `Name | CPU | MEM | NET | BLOCK | PIDs` 快照

### `docker_images`
- **参数**：`serverId`
- **返回**：同上；每行 `仓库:标签 | ID | 大小 | 创建时间`

### `docker_restart`
- **参数**：`serverId`、`container`
- **返回**：同上；**需人工确认**（短暂中断服务）

### `docker_exec`
- **参数**：`serverId`、`container`、`command`（容器内命令，如 `ps -ef`）
- **返回**：同上；**需人工确认**
- **错误约定**：`{ success: false, status, error }`，`status` 可能值：`invalid_container`、`blocked`、`rejected`、`approval_timeout`、`approval_unavailable`、`server_not_found`、`server_disabled`、`connection_error`、`failed`

## systemd 服务组（service）

`service_*` 封装 `systemctl` / `journalctl`，服务名会做字符校验并单引号转义。`service_restart` 需人工确认。

### `service_status`
- **参数**：`serverId`、`service`（服务名或 unit）
- **返回**：`{ success, status, error, output, truncated, outputChars, exitCode, durationMs }`（`systemctl status` 文本）

### `service_list`
- **参数**：`serverId`、`pattern`（可选关键字，对 unit 名 `grep -i`）
- **返回**：同上（`systemctl list-units --type=service --all --no-legend`）

### `service_restart`
- **参数**：`serverId`、`service`
- **返回**：同上；**需人工确认**

### `service_logs`
- **参数**：`serverId`、`service`、`lines`（默认 200，上限 5000）、`since`（可选，如 `1h` / `yesterday`）
- **返回**：同上（`journalctl -u <service> -n <lines>`）

## 日志文件组（log）

读取远端日志文件，**路径必须在 `security.logs.allowedPaths` 白名单内**（防止读取 `/etc/shadow` 等敏感文件），越界返回 `path_not_allowed`。单次行数受 `security.logs.maxLines` 限制。`log_tail`/`log_grep` 的路径可**直接传 `path`**，也可**传 `appId`**（用应用管理里配置的日志路径，须同时在该应用填「日志路径」）；两者都不确定时先用 `log_find` 发现。

### `log_find`
- **参数**：`serverId`、`path`（可选，限定搜索目录，须在白名单内）、`minutes`（默认 1440，只在最近 N 分钟内修改过的文件）、`maxResults`（默认 100，上限 500）
- **返回**：`{ success, status, error, serverId, serverName, host, count, truncated, files: [{ path, modifiedAt }] }` —— 按修改时间倒序
- **场景**：不确定日志路径时先调用它拿到候选文件，再用 `log_tail`/`log_grep`

### `log_tail`
- **参数**：`serverId`、`path`（日志绝对路径；与 `appId` 二选一）、`appId`（应用标识，用其配置的日志路径；与 `path` 二选一）、`lines`（默认 200）
- **返回**：`{ success, status, error, serverId, serverName, host, path, pattern, count, truncated, lines: [...] }`
- **错误约定**：`path_required`（未给 path/appId）、`log_path_not_configured`（应用没配日志路径）、`log_path_ambiguous`（应用配了多个可用路径，请用 `path` 指定）、`path_not_allowed`

### `log_grep`
- **参数**：`serverId`、`pattern`（关键字/正则）、`path` 或 `appId`（二选一）、`ignoreCase`（默认 true）、`maxMatches`（默认 200，上限 2000）
- **返回**：同上（`pattern` 回显；`lines` 为带行号的匹配行；无匹配返回 `success: true, count: 0`）

## JVM 诊断组（java）

`java_*` 通过 `ps` / `jstack` / `jcmd` / `jstat` 读取 JVM 状态，均为只读；`pid` 会做数字校验。需目标机安装 JDK（`jstack`/`jcmd`/`jstat`）。`java_threads`/`java_heap`/`java_info` 可传 `pid`，也可传 `appId` 自动解析该应用的 Java 进程（按应用名/容器名匹配；0 个返回 `pid_not_found`，多个返回 `pid_ambiguous`，不静默猜）。

### `java_processes`
- **参数**：`serverId`、`appId`（可选，只看某应用的 Java 进程）
- **返回**：`{ success, status, error, serverId, serverName, host, count, processes: [{ pid, elapsed, cpu, mem, command }] }`
- **场景**：先拿到应用 pid，再做线程/堆分析

### `java_threads`
- **参数**：`serverId`、`pid` 或 `appId`（二选一）
- **返回**：文本结果（`jstack -l <pid>` 线程栈）；分析死锁/CPU 飙高/阻塞

### `java_heap`
- **参数**：`serverId`、`pid` 或 `appId`（二选一）
- **返回**：文本结果（`jcmd <pid> GC.heap_info` + `jstat -gcutil`）；分析内存/GC/OOM

### `java_info`
- **参数**：`serverId`、`pid` 或 `appId`（二选一）
- **返回**：文本结果（`jcmd <pid> VM.version` / `VM.uptime`）

## 拓扑组（topology）

### `topology_get_overview`
- **参数**：无
- **返回**：`{ nodes: [...], edges: [...] }` —— 合并人工声明（`relations`）与自动发现（`TopologyEdges` 表）的全量拓扑
- **边的含义**：`runsOn`（应用/数据库→服务器，表示其运行在该服务器上）、`connectsTo`（应用→数据库）、`canAccess`（服务器→数据库）；`relatedTo` 为通用占位关系（无特定语义，按方向参与依赖查询）

### `topology_get_dependencies`
- **参数**：`assetId` — `ds:订单库` / `ssh:web-01` / `app:order-service`，也接受纯ID或名称
- **返回**：`{ upstream: [...], downstream: [...] }` 上下游依赖
- **典型用法**：`topology_get_dependencies("app:order-service")` → 得到它跑在哪台服务器、连了哪些数据库

### `topology_discover`
- **参数**：`serverIds`（可选，逗号分隔，留空扫描全部）、`searchPaths`（可选，空格分隔；仅允许 `security.discovery.allowedSearchPaths` 内的路径，留空时使用该白名单）
- **返回**：`{ success, result: { discovered: [...], edges: [...] } }` 并写入拓扑缓存；越界路径返回 `{ success: false, status: "path_not_allowed", error }`；已有发现在执行时返回 `{ success: false, status: "discovery_in_progress", error }`（自动发现做了节流，同一时间只跑一个）
- **发现手段**：java 进程 / 通用服务进程(nginx/mysql/redis/postgres 等, 按进程名精准匹配, 不依赖 systemd, 带监听端口) / ESTAB 网络连接 / 配置文件 JDBC(`mysql`/`postgresql`)、Redis、RabbitMQ(`amqp://`)、Kafka(`bootstrap.servers`)、Nginx(`upstream`/`proxy_pass`) / `docker ps` 容器 / MySQL `SHOW PROCESSLIST` 反查客户端
- **待确认节点**：未登记的应用/容器（`app:disc:*`）、未配置的数据库端点（`ds:disc:*`）、未登记的 DB 客户端（`ssh:disc:*`）也会作为"待确认"节点+关系写入缓存（不再只报告），可在资产关系里删除或据其补登记；人工 `relations` 不会被重复写入。
- **端口信息**：解析 `ss -ltnp` 得到监听端口（可见 root 服务进程名时精确；否则用常见端口兜底），显示在待确认节点标签与节点信息里。可开启 `security.discovery.useSudo`（安全设置）让端口/配置扫描走提权以识别 root 服务。

## 应用体检组（app）

### `app_health_snapshot`
- **参数**：`appId`（应用 ID 或名称）、`includeDiagnostics`（默认 false；true 时对依赖数据源额外做整体诊断，更慢更全）
- **返回**：`{ success, status, error, application: { id, name, type, containerName, port }, servers: [{ serverId, serverName, host, reachable, status, error, output, truncated }], datasources: [{ datasourceId, name, type, reachable, status, version, accessMode, viaTunnelServer, summary, error }], notes: [...] }`
- **说明**：应用→服务器取自 `runsOn` 关系（缺失时用应用的 `host` 兜底），应用→数据源取自 `connectsTo` 关系。服务器探测命令一次聚合 **Java 进程 + Docker 容器 + 监听端口**，并**按应用过滤**：java 段按应用名 `grep`、docker 段按 `containerName` `--filter name=`、端口段按应用的 `port` 过滤（缺失则相应段列全部）。`success` 表示"快照已产出"，具体健康看每个 server/datasource 的 `reachable`；`notes` 里会说明本次的过滤范围。
- **场景**：跨机排查某应用时**先用它**拿到全局画像，再按需下钻到 `docker_logs` / `service_logs` / `log_grep` / `java_threads` / `mysql_diagnostics` 等。

## 指南组（guide）

### `mcp_usage_guide`
- **参数**：无
- **返回**：内置使用指南 JSON（服务器/数据源意图路由、提权使用法、拓扑排查工作流、安全注意事项）
- **场景**：AI 不确定工具用法时的第一站

### `mcp_self_check`
- **参数**：`serverId`（可选，ID/名称/主机名）、`datasourceId`（可选，ID/名称）
- **返回**：`{ success, sessionId, clientName, clientVersion, checks: [{ name, status, detail }] }` —— `sessionId` 是当前 MCP 会话 ID（每次启动服务生成）、`clientName`/`clientVersion` 为当前 AI 客户端（`initialize` 握手后获取，首次工具调用前可能为空）；可用于 `ssh_get_command_history`/`datasource_get_sql_history` 的 `sessionId` 过滤；检查配置可读、审计库可写、已知主机库可读；带参数时额外做连通性测试（服务器失败会给出 `auth_failed`/`host_key_mismatch`/`timeout`/`connection_error` 分类）
- **场景**：排查"MCP 自己是否正常"、主机密钥校验不通过、配置损坏等

### `mcp_list_sessions`
- **参数**：`limit`（默认 50，上限 500）
- **返回**：`{ success, error, count, sessions: [{ sessionId, clientName, clientVersion, startedAt, lastSeenAt }] }` —— 按最近活动倒序
- **场景**：审计记录都带 `sessionId`，用它能看出"是哪个 AI 客户端、哪次连接"产生的；再配合 `ssh_get_command_history` / `datasource_get_sql_history` 的 `sessionId` 参数下钻
