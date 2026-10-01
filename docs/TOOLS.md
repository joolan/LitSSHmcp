# LitSSH MCP 工具参考

MCP 服务器当前注册 **29 个工具**，按用途分为 9 组。本文档说明每个工具的用途、参数、返回结构与选择路由；工具说明文本本身也内置了"当用户问…时使用"的意图提示（AI 客户端在 `tools/list` 时即可看到）。

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
- 敏感命令/SQL 会弹桌面确认框，超时（默认 120s）或拒绝会返回 `status: "rejected"`，不要重试轰炸。

**运行前提**：配置文件 `%APPDATA%\LitSSH\config.json`（本 App 中维护的服务器/数据源/安全策略）；命令与 SQL 审计写入 `%APPDATA%\LitSSH\audit.db`；日志在 `%APPDATA%\LitSSH\logs`。

### 工具分组（按部署裁剪）

`config.json` 的 `tools.enabledGroups` 可只暴露部分工具，降低 AI 上下文占用与误选。留空 / 不写 = 全部启用；写 `["all"]` = 全部；写 `["none"]` = 全部停用（AI 将看不到任何工具）；分组名大小写不敏感，未知分组会被忽略并在启动日志告警。桌面 App 菜单 **配置 → 工具分组设置** 可图形化勾选。

```json
{ "tools": { "enabledGroups": ["ssh", "command", "datasource", "mysql", "guide"] } }
```

| 分组键 | 中文名 | 包含的工具 |
|--------|--------|-----------|
| `ssh` | SSH 服务器 | `ssh_list_servers`、`ssh_get_server_status`、`ssh_test_connection` |
| `command` | 命令执行 | `ssh_execute_command`、`ssh_get_command_history`、`ssh_execute_sudo`、`ssh_get_sudo_status` |
| `fileTransfer` | 文件传输 | `ssh_upload_file`、`ssh_download_file`、`ssh_list_files` |
| `datasource` | 数据源 | `datasource_list`、`datasource_test_connection`、`datasource_get_sql_history` |
| `mysql` | MySQL 执行 | `mysql_query`、`mysql_execute`、`mysql_explain`、`mysql_diagnostics` |
| `postgres` | PostgreSQL 执行 | `postgres_query`、`postgres_execute`、`postgres_explain`、`postgres_diagnostics` |
| `redis` | Redis | `redis_read`、`redis_execute`、`redis_diagnostics` |
| `topology` | 拓扑 | `topology_get_overview`、`topology_get_dependencies`、`topology_discover` |
| `guide` | 指南/自检 | `mcp_usage_guide`、`mcp_self_check` |

> 注意：只启用 `mysql`/`redis` 而不启用 `datasource` 时，AI 将没有 `datasource_list` 来获取 `datasourceId`（启动日志会给出告警）。

### 工具命名约定

所有工具名遵循 **`<域>_<动作>[_<对象>]`** 的小写 snake_case。域前缀让工具在 `tools/list` 中按域聚类，便于 AI 路由与人工查找：

| 域 | 前缀 | 覆盖范围 |
|----|------|---------|
| SSH 服务器 | `ssh_` | 服务器列表 / 状态 / 连通性 / 命令执行（含 sudo）/ 文件传输 / 命令历史 |
| 数据源 | `datasource_` | 数据源列表 / 连通性 / SQL 与 Redis 审计历史 |
| MySQL | `mysql_` | 只读查询 / 写执行 / 执行计划 / 整体诊断 |
| PostgreSQL | `postgres_` | 只读查询 / 写执行 / 执行计划 / 整体诊断 |
| Redis | `redis_` | 只读命令 / 写命令 / 整体诊断 |
| 拓扑 | `topology_` | 拓扑总览 / 资产依赖 / 自动发现 |
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
| 系统架构 / 拓扑 / 应用部署在哪 / 依赖关系 | `topology_get_overview` | |
| 某资产的上下游依赖 | `topology_get_dependencies` | |
| 拓扑缺失或过期 | `topology_discover` | |
| 命令执行历史 | `ssh_get_command_history` | |
| 不确定用哪个 | `mcp_usage_guide`（返回内置使用指南） | |
| MCP是否正常 / 工具用不了 / 自检 | `mcp_self_check` | |

### ID 获取约定

- 数据库类工具的 `datasourceId` → 用 `datasource_list` 获取；
- SSH 类工具的 `serverId` → 用 `ssh_list_servers` 获取；
- 拓扑工具的 `assetId` → `ssh:xx` / `ds:xx` / `app:xx` 前缀格式，也接受纯 ID 或名称。

### 通用约定

- 所有返回均为 JSON；**除 `mcp_usage_guide`（使用指南文档）外，全部工具均提供结构化输出**（MCP `structuredContent` + `outputSchema`），并同时保留等价的 text(JSON)（向后兼容）。注意：`ssh_list_servers`/`datasource_list`/`*_history` 等由裸数组改为 `{ success, count, ... }` 包裹；`ssh_execute_command` 等命令类返回 `output`/`error`（非 stdout/stderr）；字段统一 camelCase；
- 成功/失败统一以 `success` 字段为主，配合 `status` 细分错误类型（`blocked` / `rejected` / `path_not_allowed` / `file_transfer_disabled` / `..._not_found` 等）；
- 敏感操作（敏感命令、敏感 SQL、Redis 写命令、文件传输）由桌面弹窗确认，AI 侧表现为 `status: "rejected"`；第一期 Redis 写命令**一律**确认（不按敏感度分级）；
- 每个工具带 MCP 注解 `ReadOnly` / `Destructive` / `Idempotent` / `OpenWorld`，只读工具与破坏性工具易于在客户端区分；
- 工具名遵循「工具命名约定」（`<域>_<动作>[_<对象>]`）；
- 任何工具的入参、出参都**不包含密码**；
- 所有命令与 SQL（含被拒绝的）都会写入本机审计库 `%APPDATA%\LitSSH\audit.db`；MCP 运行日志在 `%APPDATA%\LitSSH\logs`。

## SSH 服务器组（ssh）

### `ssh_list_servers`
- **参数**：无
- **返回**：`{ success, count, servers: [...] }`，每项含 `id/name/host/port/username/authType/description/tags/lastConnectedAt`（**无密码**）

### `ssh_get_server_status`
- **参数**：`serverId` — 服务器ID
- **返回**：`{ success, id, name, host, status }`（`status` 为 `Connected`/`Disconnected`）

### `ssh_test_connection`
- **参数**：`serverId` — 服务器ID
- **返回**：`{ success, serverId, name }`
- **说明**：只测 SSH 连通性；测数据库请用 `datasource_test_connection`

## 命令执行组（command）

### `ssh_execute_command`
- **参数**：`serverId`（服务器ID）、`command`（Shell命令）
- **返回**：`{ success, status, error, output, exitCode, durationMs }`（`output` 为命令输出；被拒绝/禁止时 `status` 为 `rejected`/`blocked`）
- **安全链路**：黑名单直接拒绝 → 敏感命令弹桌面确认 → 执行 → 写审计

### `ssh_execute_sudo`
- **参数**：`serverId`、`command`
- **返回**：同上；未配置提权时返回 `status: "sudo_not_configured"`
- **说明**：主动判断需要提权时直接用本工具，不要先 `ssh_execute_command` 失败再提权

### `ssh_get_sudo_status`
- **参数**：`serverId`
- **返回**：`{ success, serverId, serverName, sudoType, sudoUsername, isConfigured, description }`

### `ssh_get_command_history`
- **参数**：`serverId`（可选，不传查全部）、`limit`（默认 50）
- **返回**：`{ success, count, records: [...] }`

**命令类错误约定**：`{ success: false, error, status }`，`status` 可能值：`blocked`（被禁止）、`rejected`（用户拒绝）、`server_not_found`、`sudo_not_configured`、`file_not_found`。

## 文件传输组（fileTransfer）

上传/下载受 `fileTransfer.enabled` 开关与 `allowedLocalPaths` / `allowedRemotePaths` 白名单约束（越界返回 `path_not_allowed`，禁用返回 `file_transfer_disabled`），路径会做规范化并拦截 `..` 穿越。上传/下载过程中会通过 MCP `notifications/progress` 推送进度（字节数/百分比）。

### `ssh_upload_file`
- **参数**：`serverId`、`localPath`（本地路径，须在 `allowedLocalPaths` 内）、`remotePath`（远程路径，须在 `allowedRemotePaths` 内）
- **返回**：`{ success, message, bytesTransferred, durationMs, ... }`；越界返回 `path_not_allowed` 并附允许路径；超过 `fileTransfer.maxFileSizeBytes` 返回 `file_too_large`

### `ssh_download_file`
- **参数**：`serverId`、`remotePath`（须在 `allowedRemotePaths` 内）、`localPath`（须在 `allowedLocalPaths` 内）
- **返回**：`{ success, message, bytesTransferred, durationMs, ... }`

### `ssh_list_files`
- **参数**：`serverId`、`remotePath`（远程目录）
- **返回**：`{ success, path, files: [{ name, fullName, size, lastModified, isDirectory, isSymbolicLink }] }`

## 数据源组（datasource）

### `datasource_list`
- **参数**：无
- **返回**：`{ success, count, dataSources: [...] }`，每项含 `id/name/type/host/port/username/defaultDatabase/accessMode/tunnelServerId/tunnelServer/description/tags/accessibleFromSshServers/connectedApplications`
- **说明**：**密码永不出现在返回中**；`type` 为 `mysql` / `postgres` / `redis`（按类型使用对应组的工具）；`host/port/username` 可直接用于与应用日志中的连接串比对

### `datasource_test_connection`
- **参数**：`datasourceId` — 数据源ID（可用 `datasource_list` 列出）
- **返回**：`{ success, datasourceId, name, host, port, accessMode, viaTunnelServer, version, durationMs, error }` —— 自动选择直连或建 SSH 隧道

### `datasource_get_sql_history`
- **参数**：`datasourceId`（可选）、`limit`（默认 50）
- **返回**：`{ success, count, records: [...] }`（含被拒绝/被驳回的操作）
- **说明**：Redis 命令审计也写在同一张表，`operation` 为 `Query`/`Execute`/`Diagnostics`，SQL 列是命令原文

## MySQL 执行组（mysql）

所有 MySQL 工具都要求先有数据源配置（WPF 界面或 `config.json` 录入），且入参只有 `datasourceId`，**不含账号密码**。

### `mysql_query`（只读）
- **参数**：`datasourceId`、`sql`（只读语句：SELECT/SHOW/EXPLAIN/DESC/WITH）、`maxRows`（默认 100，上限 1000）
- **返回**：`{ success, datasourceId, name, columns, rows, rowCount, truncated, durationMs, error }`
- **限制**：单条语句、非只读语句返回 `status: "blocked"` 并提示改用 `mysql_execute`

### `mysql_execute`（写操作）
- **参数**：`datasourceId`、`sql`（单条 INSERT/UPDATE/DELETE/DDL）
- **返回**：`{ success, affectedRows, durationMs, ... }`
- **安全链路**：危险语句（DROP/无WHERE的DELETE等）直接拒绝 → 敏感语句弹桌面确认 → 执行 → 全部写审计
- **提示**：只读语句传入会返回 `status: "readonly_statement"`，提示改用 `mysql_query`

### `mysql_explain`
- **参数**：`datasourceId`、`sql`（SELECT语句）
- **返回**：`{ success, columns, rows, durationMs }`（EXPLAIN 计划表）

### `mysql_diagnostics`
- **参数**：`datasourceId`
- **返回**：`{ success, connections, slowQueries, lockWaits, replication, processlist, ... }`
- **场景**：用户问"MySQL正不正常/为什么慢/卡住/连接数暴涨"时**优先用它**做整体诊断，再按需下钻

**MySQL 错误约定**：`{ success: false, status, error }`，`status` 可能值：`datasource_not_found`、`unsupported_type`、`blocked`、`readonly_statement`、`rejected`。

## PostgreSQL 执行组（postgres）

所有 PostgreSQL 工具都要求数据源 `type = postgres`（桌面 App"添加数据源"→类型选 PostgreSQL，端口默认 5432），入参只有 `datasourceId`，**不含账号密码**。连接自动选择直连或经 SSH 隧道（复用同一套隧道/限流/密钥校验链路）。仅读/写/敏感过滤与 MySQL 共用同一套 SQL 安全策略。

### `postgres_query`（只读）
- **参数**：`datasourceId`、`sql`（只读语句：SELECT/SHOW/EXPLAIN/WITH）、`maxRows`（默认 100，上限 1000）
- **返回**：`{ success, datasourceId, name, columns, rows, rowCount, truncated, durationMs, error }`
- **限制**：单条语句、非只读语句返回 `status: "blocked"` 并提示改用 `postgres_execute`

### `postgres_execute`（写操作）
- **参数**：`datasourceId`、`sql`（单条 INSERT/UPDATE/DELETE/DDL）
- **返回**：`{ success, affectedRows, durationMs, ... }`
- **安全链路**：危险语句（DROP/无WHERE的DELETE等）直接拒绝 → 敏感语句弹桌面确认（或按数据源 `WriteApproval=AutoApprove` 放行）→ 执行 → 全部写审计
- **提示**：只读语句传入会返回 `status: "readonly_statement"`；数据源只读时返回 `status: "readonly_datasource"`

### `postgres_explain`
- **参数**：`datasourceId`、`sql`（SELECT语句）
- **返回**：`{ success, columns, rows, durationMs }`（EXPLAIN 计划）

### `postgres_diagnostics`
- **参数**：`datasourceId`
- **返回**：`{ success, summary, data, ... }` —— 含版本/当前库、连接数(当前/活动/上限)、活动会话按状态、最耗时查询、等待锁、复制、死锁、缓存命中率、数据库大小
- **场景**：用户问"PG 正不正常 / 为什么慢 / 卡 / 连接数暴涨"时**优先用它**做整体诊断

**PostgreSQL 错误约定**：`{ success: false, status, error }`，`status` 可能值：`datasource_not_found`、`unsupported_type`、`blocked`、`readonly_statement`、`readonly_datasource`、`rejected`。

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

**Redis 错误约定**：`{ success: false, status, error }`，`status` 可能值：`datasource_not_found`、`unsupported_type`、`blocked`、`not_readonly`、`readonly_statement`、`rejected`、`invalid_command`、`redis_error`（Redis 返回的错误）、`connection_error`。

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
- **返回**：`{ success, result: { discovered: [...], edges: [...] } }` 并写入拓扑缓存；越界路径返回 `{ success: false, status: "path_not_allowed", error }`
- **发现手段**：java 进程 / ESTAB 网络连接 / 配置文件 JDBC 地址 / `docker ps` 容器 / MySQL `SHOW PROCESSLIST` 反查客户端

## 指南组（guide）

### `mcp_usage_guide`
- **参数**：无
- **返回**：内置使用指南 JSON（服务器/数据源意图路由、提权使用法、拓扑排查工作流、安全注意事项）
- **场景**：AI 不确定工具用法时的第一站

### `mcp_self_check`
- **参数**：`serverId`（可选，测该服务器连通性）、`datasourceId`（可选，测该数据源连通性）
- **返回**：`{ success, checks: [{ name, status, detail }] }` —— 检查配置可读、审计库可写、已知主机库可读；带参数时额外做连通性测试
- **场景**：排查"MCP 自己是否正常"、主机密钥校验不通过、配置损坏等
