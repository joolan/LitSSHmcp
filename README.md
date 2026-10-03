# LitSSH MCP

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows-blue.svg)](https://windows.com/)
[![AI Assistant](https://img.shields.io/badge/AI%20Assistant-Opencode-blue.svg)](https://opencode.ai/)

Windows平台下的SSH MCP服务器，让AI智能体可以安全地通过SSH管理远程服务器，并集成 MySQL / PostgreSQL / Redis 数据源与资产拓扑，支撑跨服务器/应用(含Docker容器)/数据库的全链路故障排查。

> 🤖 本项目使用 [Opencode](https://opencode.ai/) AI助手开发

## 📚 文档导航

| 文档 | 内容 |
|------|------|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | 架构设计：分层结构、核心模块、安全模型、拓扑模型、线程模型、扩展点 |
| [docs/TOOLS.md](docs/TOOLS.md) | 49个MCP工具完整参考：参数、返回结构、"用户意图→工具"路由表 |
| [docs/CHANGELOG.md](docs/CHANGELOG.md) | 迭代历史：每个版本的新增/修复/变更记录 |
| [docs/UPGRADE_PLAN.md](docs/UPGRADE_PLAN.md) | 升级方案（Roadmap）：安全、可视化配置、运维、质量的分期计划 |

## ✨ 功能特性

### MCP Tools (AI可调用的工具)

> 完整的工具参数/返回/用法与"用户意图 → 工具"路由表见 [docs/TOOLS.md](docs/TOOLS.md)（**唯一事实来源**），也可在 App 菜单 **MCP工具说明** 中查看并一键复制到提示词。工具发生变动时，MCP 服务器端注解、`docs/TOOLS.md`、App 展示三处必须同步（各处均有"同步约定"注释）。各工具 `[Description]` 保持精简（1~2 句 + 关键互斥提示），详细路由以 `docs/TOOLS.md` 为准，避免工具清单挤占 AI 上下文。

| Tool | 描述 |
|------|------|
| `ssh_list_servers` | 列出所有已配置的SSH服务器（已禁用的服务器不会出现） |
| `ssh_execute_command` | 在指定服务器执行Shell命令 |
| `ssh_execute_sudo` | 使用提权执行命令（权限不足时使用） |
| `ssh_get_sudo_status` | 获取服务器提权配置状态 |
| `ssh_get_server_status` | 获取服务器连接状态 |
| `ssh_test_connection` | 测试SSH连接 |
| `ssh_get_command_history` | 查看命令执行历史 |
| `ssh_upload_file` | 上传本地文件到服务器 |
| `ssh_download_file` | 从服务器下载文件到本地 |
| `ssh_list_files` | 浏览服务器目录 |
| `datasource_list` | 列出MySQL/PostgreSQL/Redis等数据源（主机/端口/账号/绑定关系，**无密码**） |
| `datasource_test_connection` | 测试数据库连通性（直连或SSH隧道） |
| `mysql_query` | 只读SQL查询（SELECT/SHOW/EXPLAIN） |
| `mysql_execute` | 写SQL（危险语句拒绝，敏感语句桌面审批） |
| `mysql_explain` | SQL执行计划分析 |
| `mysql_diagnostics` | MySQL诊断：连接数/慢查询/锁/复制/进程列表 |
| `postgres_query` | 只读SQL查询（PostgreSQL） |
| `postgres_execute` | 写SQL（PostgreSQL，危险语句拒绝，敏感语句审批） |
| `postgres_explain` | SQL执行计划分析（PostgreSQL） |
| `postgres_diagnostics` | PostgreSQL诊断：连接/活动会话/等待锁/复制/缓存命中/死锁 |
| `redis_read` | Redis只读命令（GET/HGETALL/INFO/SCAN/SLOWLOG等白名单） |
| `redis_execute` | Redis写/管理命令（一律桌面审批，危险命令直接拒绝） |
| `redis_diagnostics` | Redis诊断：内存/客户端/命中率/键空间/慢日志/主从/持久化 |
| `datasource_get_sql_history` | SQL与Redis命令审计历史（带 sessionId/tool，可过滤） |
| `docker_ps` | 列出Docker容器（结构化：名称/镜像/状态/端口） |
| `docker_logs` | 查看容器日志 |
| `docker_inspect` | 容器详情（inspect：环境/挂载/网络/健康检查） |
| `docker_stats` | 容器资源占用快照（CPU/内存/网络/磁盘IO） |
| `docker_images` | 镜像列表（版本/大小） |
| `docker_restart` | 重启容器（需审批） |
| `docker_exec` | 容器内执行命令（需审批） |
| `service_status` | systemd 服务状态 |
| `service_list` | 列出 systemd 服务 |
| `service_restart` | 重启 systemd 服务（需审批） |
| `service_logs` | 服务日志（journalctl） |
| `log_tail` | 查看日志文件尾部（可传 path 或 appId） |
| `log_grep` | 日志按关键字/正则检索（可传 path 或 appId） |
| `log_find` | 发现最近修改的日志文件（不知路径时先用它） |
| `java_processes` | 列出 Java 进程（可传 appId 过滤） |
| `java_threads` | 抓取线程栈（jstack，可传 appId 自动解析 pid） |
| `java_heap` | 堆内存/GC（jcmd + jstat） |
| `java_info` | JVM 版本/运行时长 |
| `app_health_snapshot` | 按应用聚合体检（所在服务器进程/容器/端口 + 依赖数据源连通性） |
| `topology_get_overview` | 资产拓扑图（服务器/应用/数据库及关系） |
| `topology_get_dependencies` | 查询某资产的上下游依赖 |
| `topology_discover` | 自动发现拓扑（java/服务进程/端口/ESTAB/JDBC/Redis/RabbitMQ/Kafka/Nginx/docker/processlist；有节流） |
| `mcp_usage_guide` | 获取使用指南 |
| `mcp_self_check` | MCP 自检（配置/审计/主机密钥，可测连通性；返回当前会话ID/客户端） |
| `mcp_list_sessions` | 列出最近 MCP 会话（会话ID/客户端/首末活动） |

各工具的适用场景与参数详见 [docs/TOOLS.md](docs/TOOLS.md)。

**工具分组（可选，按部署裁剪）**：默认暴露全部 49 个工具；可在 App 菜单 **配置 → 工具分组设置** 勾选，或直接改 `config.json` 的 `tools.enabledGroups`，只启用需要的分组（`ssh` / `command` / `fileTransfer` / `datasource` / `mysql` / `postgres` / `redis` / `docker` / `service` / `log` / `java` / `topology` / `app` / `guide`），降低 AI 上下文占用与误选。留空/不写 = 全部，`["all"]` = 全部，`["none"]` = 全部停用。分组与工具对应表见 [docs/TOOLS.md](docs/TOOLS.md)。

**AI 排障 skill**：仓库内置 [`docs/litssh-mcp-ops-skill/SKILL.md`](docs/litssh-mcp-ops-skill/SKILL.md)（工具无关，随仓库分发），供支持 skills 的 AI 智能体（opencode / Claude 等）使用。内容包含工具路由、标准排障流程、日志路径发现，以及 **MCP 未覆盖能力经 SSH 变通**的方案（如按 Java 启动命令/配置文件定位日志后再用 `log_tail`）。可复制到对应智能体的 skill 目录，或在 opencode 中用 `skills.paths` 指向该目录。

### 安全控制

- **全局开关**: 安全设置中的「启用 MCP 服务」关闭后**拒绝所有工具调用**（`security.enabled`，按配置热生效、无需重启）
- **禁止命令列表**: 直接拒绝执行危险命令
- **敏感命令列表**: 弹出桌面窗口提示用户确认后执行
- **授权确认弹窗**: 置顶确认框，默认 **45 秒无操作自动拒绝**；可选独立子进程/原生弹窗样式（`security.approval`）
- **审批通道**: `security.approval.channels` 默认 `["desktop","cli"]` —— 无桌面/headless 时操作员用 `litssh approvals` 查看、`litssh approve <id>` / `litssh deny <id>` 决定（首个决定者生效；超时→`approval_timeout`，无可用通道→`approval_unavailable`）
- **审批模式**: `security.approval.mode` —— `manual`（默认，需人工处理）/ `auto-approve`（危险：所有触发审批的操作自动放行）/ `auto-reject`（触发审批时直接拒绝）；桌面 App「安全设置 → 审批模式」可切换。只影响“需人工确认”的敏感操作，命令过滤器硬拒绝（`blocked`）不受影响
- **审计会话/工具区分**: 每次启动 MCP 服务生成会话 ID，命令/SQL 审计带 `sessionId` 与 `tool`（哪个工具产生），并参与哈希链防篡改；可用 `mcp_list_sessions` / `*_history` 过滤
- **文件传输审批**: 上传/下载需用户确认，并受**本地/远程路径白名单**与大小上限约束（`allowedLocalPaths` / `allowedRemotePaths` / `maxFileSizeBytes`）
- **主机密钥校验(TOFU)**: 首次连接记录 SSH 主机指纹，之后指纹变化即拒绝（`security.sshHostKey.mode = tofu|strict|off`）
- **按目标限流**: 单服务器/数据源的并发数与每分钟调用上限（`security.limits`）
- **审计日志**: 记录所有命令与 SQL（含被拒绝的），支持 SQL 原文开关、字面量脱敏、**超期记录归档到历史表永久保留**；审计写入 **HMAC-SHA256 哈希链**，可在「审计日志」中**校验完整性**检测篡改（`security.audit`）；每条记录带 **MCP 会话 ID**（每次启动 MCP 服务生成），便于按会话区分
- **提权执行**: 权限不足时可使用sudo提权
- **凭据隔离**: 数据库/SSH账号密码仅保存在MCP本机（DPAPI加密落盘），任何MCP工具的入参与出参都不包含密码，AI智能体只能通过`datasourceId`引用数据源
- **SQL安全过滤**: `mysql_query`仅允许只读语句；`mysql_execute`中无WHERE的DELETE/UPDATE、DROP TABLE/DATABASE、GRANT等直接拒绝，其余敏感写语句需用户桌面确认
- **Redis安全策略**: `redis_read`只放行只读白名单命令；`redis_execute`第一期**所有写操作一律桌面审批**，FLUSHALL/SHUTDOWN/DEBUG/SUBSCRIBE等危险与阻塞类命令直接拒绝（不会执行），Redis命令同样写审计
- **Docker 安全规则**: 默认拒绝 `docker system prune`/`docker volume rm`/`docker network prune`/`docker run --privileged` 等；`docker rm/rmi/kill/stop/restart/run/exec/compose down` 等写操作需桌面确认（只读的 `docker ps/logs/inspect/stats` 不受限）

## 🚀 快速开始

### 1. 编译项目

```bash
dotnet build LitSSHmcp.slnx
dotnet test LitSSHmcp.slnx   # 全部测试(Core + MCP 服务器 + App，共 184 用例)
```

### 2. 发布为独立exe

```bash
dotnet publish src/LitSSHmcp.McpServer -c Release -r win-x64 --self-contained -o publish
```

发布后的文件位于：`publish/LitSSHmcp.McpServer.exe`

### 3. 配置SSH服务器

**方式一：使用WPF管理界面**

```bash
dotnet run --project src/LitSSHmcp.App
```

打开管理界面后：顶部菜单分为 **资产**（数据源管理 / 应用管理 / 资产拓扑(可视化编辑)）、**安全**（安全设置）、**审计**（审计日志）、**配置**（工具分组设置 / 导出配置 / 导入配置）、**MCP工具说明**（MCP 介绍 + 49 个工具的用途/参数/用法与意图路由）。

主界面为轻量客户端布局：**左侧**是 SSH 服务器列表（每项两行显示 名称 + `主机:端口`），顶部仅 **添加 / 刷新**，条目**右键菜单**为 连接 / 编辑 / 删除，**双击**即连接。连接后在右侧打开一个**会话标签页**：可执行命令、查看输出与最近活动、测试连接，标签顶部 `✕` 可关闭会话。

- **数据源管理**：维护 MySQL / PostgreSQL / Redis 等数据源；顶部仅 **添加 / 刷新**，条目标**右键菜单**为 测试连接 / 编辑 / 删除，双击也可编辑（类型切换时自动带出对应默认端口 3306/5432/6379）；可设置**治理**项（只读、最大行数、超时、写审批策略）。密码仅本机 DPAPI 加密保存、不对 AI 开放。删除时会提示并级联清理引用它的关系。
- **应用管理**：维护 `app:` 应用节点（名称/类型/端口/主机/描述）；Docker 应用把类型填 `docker` 并填**容器名**（与 `docker ps` 的 NAMES 一致），AI 即可用 SSH 工具管理；顶部 **添加 / 刷新**，条目**右键菜单**为 编辑 / 删除，双击也可编辑。
- **资产拓扑（可视化编辑）**：一张可交互画布，服务器/应用/数据库及关系可视化（`runsOn` 内嵌、`connectsTo`/`canAccess` 避障正交连线 + 圆点/箭头/过桥）。**可直接编辑**：
  - **拖动**节点（拖服务器带动其内子节点，子节点限制在容器内；位置按「起点 + 总位移」绝对推导，往返拖动**不漂移**）；**缩放**节点（四角手柄 + **四边内侧直接拉伸**，有最小尺寸）；
  - 选中节点后从其**边中点端口**（圆点位于边外侧，节点边界本身用于拉伸缩放）**拖到另一节点**创建关系（自动推断类型：应用/库→服务器=`runsOn`、应用→库=`connectsTo`、服务器→库=`canAccess`，其余=`relatedTo`，并做逻辑校验）；
  - **点选连线**后右侧出现**关系属性面板**：可修改**关系类型**、填写/编辑**备注**并保存（手动关系写入 `config.Relations`）；也可**删除**（手动关系写 `config.Relations`；自动发现边清理 `TopologyEdges` 缓存；或选中连线后按 `Delete`）。连线在服务器节点内也能直接点选；
  - **无限画布**：滚轮缩放、**左键按住空白处拖动平移**、右上角**「更多操作 ▾」**下拉（`＋ / － / 适应 / 100%`（`Ctrl+0` 重置）/ 刷新 / **导出图片…**，DEBUG 另有压测）；画布带**随缩放/平移实时对齐的网格背景**（可越过左/上边界），节点移动与调整大小均**自动吸附 10px 网格**；关系属性面板可拖动标题栏移动位置；**选中节点后可用方向键微调位置**（10px/次，带容器校验）；指针移到节点四边/四角时鼠标变为对应**缩放光标**（**四边按住即可拉伸调整该侧**）、移到节点上变为移动光标；
  - **拖动节点进出服务器**：拖入服务器且**完全落入**时自动建立 `runsOn`（不合法则**禁止移入**并提示）；从服务器内拖出会**弹窗确认删除** `runsOn`；**任何节点不得与服务器部分重叠**（必须完全在内或完全在外，否则落点无效并回退）；**调整大小**同样受限：普通节点不得与服务器重叠、**服务器调整不得与任何其它节点接触**，与其它节点接触时就地停住（反向拖动可继续、松开再校验一次，非法则还原）；**服务器缩小时托管子节点自动收紧到新矩形**（不参与碰撞校验、不会挡住缩放）；托管子节点可在所属服务器内**自由移动/缩放**（松开自动夹回容器内）；连线仅在必要时显示类型文字（**canAccess / connectsTo 不再标注**）；点击优先命中**叶子节点**（避免连线盖住节点导致拖不动）；**托管（runsOn）的应用/数据库用点线边框**区分（选中框为蓝色长虚线）；已手动设置的连线端点会**跟随节点固定在该侧、不漂移**；
  - **节点右键 → 查看/编辑关系**：列出与选中节点相关的全部关系（手动/自动发现），可**删除**或**编辑类型与备注**（保存时经 `RelationRules` 校验）；列表以**节点名称**展示、悬浮显示完整 ID，**当前节点用红色加粗**区分；**解除 `runsOn`** 后相关应用/数据库会自动挪到**就近空白处**（不停留在原服务器内）。同一连接点上的多条连线**允许重叠**（不再分道错位，更整洁）。
  - **网格吸附**、**Ctrl+Z 撤销 / Ctrl+Y 重做**、**「重新自动布局」**；
  - 手动布局（节点位置/尺寸、端点锚点）保存在 `%APPDATA%\LitSSH\topology-layout.json`，与应用/数据源配置分离。
  - 关系逻辑校验（`runsOn` 只能 应用/数据库→服务器且**每节点只 runsOn 一台**、`connectsTo` 只能 应用→数据库、`canAccess` 只能 服务器→数据库、禁止自环）在创建时即时生效。
  - **性能诊断**：设置环境变量 `LITSSH_PERF=1` 后再操作画布，会把 `CommitLayout` / `RebuildEdges` 耗时、拖动会话汇总（`DragSession`，VM 每帧耗时）与帧率探针（`FrameProbe`，含 `tier=0` 软件渲染/RDP 标记）写入 `%APPDATA%\LitSSH\topology-perf.log`，二者对照即可区分瓶颈在 VM 还是渲染层（默认关闭、零开销）。
- **安全设置**：可视化编辑命令/SQL 过滤、文件传输、主机密钥、限流、审计策略、**授权弹窗样式与审批通道**、**查询结果列级脱敏**。
- **审计日志**：查看命令/SQL 审计；支持按 服务器/数据源ID 筛选 + **命令关键字模糊查询** + **按会话ID 筛选**、复制、导出 CSV、**含归档**（超期记录永久保留）与 **校验完整性**（哈希链防篡改）。
- **MCP工具说明**：在 AI 智能体里更准确地使用 MCP —— 展示 MCP 接入配置（stdio + 客户端配置样例）、意图 → 工具路由表，以及 49 个工具的参数/返回/使用要点；左侧按分组浏览、可搜索，支持**复制本节 / 复制全部说明**粘贴到提示词。内容与 `docs/TOOLS.md`、MCP 服务器端工具注解**双向同步**（工具变动时三处一起改，代码内有同步约定注释）。

**方式二：手动编辑配置文件**

配置文件位置：`%APPDATA%\LitSSH\config.json`

```json
{
  "servers": [
    {
      "id": "my-server",
      "name": "我的服务器",
      "host": "192.168.1.100",
      "port": 22,
      "username": "root",
      "authType": "Password",
      "password": "your-password",
      "sudoType": "CurrentUser",
      "sudoPassword": "your-sudo-password"
    }
  ],
  "dataSources": [
    {
      "id": "mysql-order-01",
      "name": "订单库",
      "type": "mysql",
      "host": "192.168.1.101",
      "port": 3306,
      "username": "order_app",
      "password": "your-db-password",
      "defaultDatabase": "orders",
      "accessMode": "sshTunnel",
      "tunnelServerId": "my-server"
    },
    {
      "id": "redis-cache-01",
      "name": "缓存",
      "type": "redis",
      "host": "192.168.1.101",
      "port": 6379,
      "username": "",
      "password": "your-redis-password",
      "defaultDatabase": "0",
      "accessMode": "sshTunnel",
      "tunnelServerId": "my-server"
    },
    {
      "id": "pg-report-01",
      "name": "报表库",
      "type": "postgres",
      "host": "192.168.1.101",
      "port": 5432,
      "username": "report_app",
      "password": "your-pg-password",
      "defaultDatabase": "report",
      "accessMode": "sshTunnel",
      "tunnelServerId": "my-server",
      "readOnly": true,
      "maxRows": 500,
      "timeoutSeconds": 30,
      "writeApproval": "Always"
    }
  ],
  "applications": [
    { "id": "order-service", "name": "订单服务", "type": "java", "port": 8080, "host": "192.168.1.100" },
    { "id": "order-worker", "name": "订单Worker", "type": "docker", "containerName": "order-worker", "host": "192.168.1.100" }
  ],
  "relations": [
    { "from": "app:order-service", "to": "ssh:my-server", "type": "runsOn" },
    { "from": "app:order-worker", "to": "ssh:my-server", "type": "runsOn" },
    { "from": "app:order-service", "to": "ds:mysql-order-01", "type": "connectsTo" },
    { "from": "ssh:my-server", "to": "ds:mysql-order-01", "type": "canAccess" }
  ],
  "tools": {
    "enabledGroups": ["ssh", "command", "datasource", "mysql", "postgres", "redis", "docker", "service", "log", "java", "topology", "app", "guide"]
  },
  "security": {
    "enabled": true,
    "commandFilter": {
      "blockedCommands": ["rm -rf /", "mkfs", "dd if=/dev/zero"],
      "sensitiveCommands": ["rm ", "chmod", "reboot", "shutdown"],
      "sensitivePatterns": ["\\brm\\b", "\\bchmod\\b"]
    },
    "sqlFilter": {
      "blockedPatterns": ["\\btruncate\\b", "\\bgrant\\b"],
      "sensitivePatterns": ["\\binsert\\b", "\\bupdate\\b", "\\bdelete\\b"]
    },
    "fileTransfer": {
      "enabled": true,
      "requireApproval": true,
      "maxFileSizeBytes": 104857600,
      "allowedLocalPaths": ["C:\\Users\\you\\Desktop"],
      "allowedRemotePaths": ["/home", "/tmp", "/var/log"]
    },
    "sshHostKey": { "mode": "tofu" },
    "discovery": { "allowedSearchPaths": ["/opt", "/home", "/srv", "/app", "/data", "/etc/nginx"], "useSudo": false },
    "limits": { "maxConcurrentPerTarget": 3, "maxCallsPerMinutePerTarget": 60 },
    "audit": { "storeSqlText": true, "maskLiterals": false, "retentionDays": 90 },
    "masking": { "rules": [ { "column": "phone|mobile", "mode": "phone" } ] },
    "approval": { "style": "process", "mode": "manual", "channels": ["desktop", "cli"], "timeoutSeconds": 45, "topMost": true }
  }
}
```

> 所有明文密码在保存/加载时会自动迁移为DPAPI密文（`enc:`前缀），仅当前Windows用户可解密。完整字段示例见 [`config/config.example.json`](config/config.example.json)；访问模式、拓扑关系类型、审批弹窗样式等含义详见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

### 4. 在AI客户端中配置MCP

#### Claude Desktop

编辑配置文件 `%APPDATA%\Claude\claude_desktop_config.json`：

```json
{
  "mcpServers": {
    "litssh": {
      "command": "C:\\path\\to\\publish\\LitSSHmcp.McpServer.exe"
    }
  }
}
```

#### Cursor / Windsurf

编辑 `.cursor/mcp.json`：

```json
{
  "mcpServers": {
    "litssh": {
      "command": "C:\\path\\to\\publish\\LitSSHmcp.McpServer.exe"
    }
  }
}
```

#### Cline (VS Code)

编辑 `.vscode/mcp.json`：

```json
{
  "servers": {
    "litssh": {
      "command": "C:\\path\\to\\publish\\LitSSHmcp.McpServer.exe"
    }
  }
}
```

#### CodeBuddy

通过界面配置或编辑 `~/.codebuddy/.mcp.json`：

```json
{
  "mcpServers": {
    "litssh": {
      "type": "stdio",
      "command": "C:\\path\\to\\publish\\LitSSHmcp.McpServer.exe"
    }
  }
}
```

#### 开发模式（使用dotnet run）

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

### 5. 验证配置

重启AI客户端后，输入以下内容测试：

```
请列出所有SSH服务器
```

AI会调用 `ssh_list_servers` 工具返回服务器列表。

## 📖 使用示例

### 执行命令

```
在Web服务器上执行 df -h 查看磁盘空间
```

### 提权执行

```
查看nginx进程状态（需要root权限）
```

AI会自动使用 `ssh_execute_sudo` 工具提权执行。

### 文件传输

```
把本地的 config.yml 上传到服务器的 /etc/nginx/ 目录
```

```
下载服务器上的 /var/log/nginx/access.log 到桌面
```

### 数据源与拓扑排查

```
订单服务报错了，帮我排查一下
```

AI的典型排查链路：

1. `topology_get_overview` 拿到全局拓扑：订单服务（Java）运行在哪台SSH服务器、连接了哪个MySQL
2. `topology_get_dependencies(app:order-service)` 精确获取上下游依赖
3. SSH侧：`ssh_execute_command` 查看 java 进程、端口、应用日志中的数据库连接异常
4. 数据库侧：`mysql_diagnostics` 看连接数/慢查询/锁等待/复制状态，`mysql_query` 查 `SHOW FULL PROCESSLIST` 与慢日志，`mysql_explain` 分析问题SQL；涉及缓存时用 `redis_diagnostics` 看内存/命中率/慢日志、`redis_read` 查具体key
5. 跨机关联：应用日志里的数据库IP与 `datasource_list` 返回的 `host/port` 对应，即可确认是哪个数据源（MySQL 3306 / Redis 6379 皆可匹配）
6. 拓扑过期时用 `topology_discover` 自动补全（扫描 java/通用服务进程、监听端口、ESTAB 连接、配置文件 JDBC/Redis/RabbitMQ/Kafka/Nginx、docker 容器、MySQL processlist）；发现做了**节流**（同时只跑一个）；未登记的应用/数据源/客户端会以 `*:disc:*` "待确认"节点出现，可在拓扑页**右键 → 确认节点**登记为资产。

### 资产拓扑可视化

在 WPF 界面 **资产 → 资产拓扑(可视化编辑)** 中查看与编辑：

- **`runsOn` 以嵌套呈现**：应用与数据库若声明了 `runsOn` 关系，会显示在所属服务器区块内部（一眼看出某台服务器上运行着哪些应用/MySQL）；服务器标题下标注 `N 应用 / M 数据库`。
- **端口展示**：节点标题带端口（如 `订单库 :3306`、`缓存 :6379`、`web-01 :22`）。
- **连线带类型**：`connectsTo`（应用→数据库，蓝）、`canAccess`（服务器→数据库，绿）；采用**避障正交路由**（不直穿其它节点；端点从四边中点择优，可进入 `runsOn` 嵌套区块；找不到路径回退 Z 形），拐角圆角化，起点圆点、终点箭头；**交叉处过桥**，同一连接点/走廊的多条连线**允许重叠**（不再分道错位）；`topology_discover` 自动发现的关系用虚线区分。连线绘制在服务器区块之上、叶子节点之下。
- **悬浮看详情**：鼠标悬停节点显示主机/端口/账号/类型/描述/标签（**密码等敏感信息不展示**）。
- 多个应用连接同一个数据库会各自绘制一条 `connectsTo`。

关系示例：

```
app:order-service  --runsOn-->     ssh:web-server-01    # 应用运行在服务器
ds:mysql-order-01  --runsOn-->     ssh:db-server-01     # MySQL 运行在服务器(数据库也可 runsOn)
app:order-service  --connectsTo--> ds:mysql-order-01    # 应用连接数据库
ssh:web-server-01  --canAccess-->  ds:mysql-order-01    # 服务器可访问数据库
```

### 授权确认弹窗

敏感操作（敏感命令、敏感 SQL、文件传输）会弹出确认框，默认 **置顶** 且 **45 秒无操作自动拒绝**。样式由 `security.approval.style` 控制：

- `process`（推荐）：启动独立子进程显示弹窗，规避部分宿主（如 Electron 客户端）的隐藏窗口问题；
- `dialog`：MCP 进程内显示；
- `native`：原生置顶 MessageBox（无超时）。

审批**通道**由 `security.approval.channels` 控制（默认 `["desktop","cli"]`）：`desktop`（本机弹窗）/ `cli`（带外，操作员用 `litssh approvals` 查看、`litssh approve/deny <id>` 决定）；可同时启用，**首个给出决定者生效**。结果为 `rejected`（拒绝）/ `approval_timeout`（超时）/ `approval_unavailable`（无可用通道）。

## 🔐 提权配置

### 提权方式

| 提权方式 | 说明 | 适用场景 |
|---------|------|---------|
| 不启用 | 不使用提权 | 普通用户操作 |
| 当前用户sudo | 使用SSH用户密码执行sudo | CentOS/Ubuntu/Debian常用 |
| root用户 | 切换到root用户 | 需要root密码 |
| 指定用户 | 切换到指定用户 | 需要该用户密码 |
| 自动 | 先试「当前用户sudo」，失败再试「su - root」（同一提权密码） | 不确定账号是否在 sudoers / 是否配了root密码时；推荐 

### Linux系统sudo机制

- `sudo command` 需要输入**当前SSH用户**的密码
- 如果sudoers配置了 `NOPASSWD`，则不需要密码
- root用户执行sudo不需要密码

## 🛠️ 命令行工具 (CLI)

LitSSH提供命令行SSH连接工具，可直接在终端连接服务器。

### 发布CLI工具

```bash
dotnet publish src/LitSSHmcp.Cli -c Release -r win-x64 --self-contained -o publish
```

### 使用方法

```bash
# 列出所有服务器
litssh list

# 连接到服务器(交互式)
litssh connect web-server

# 在服务器上执行单条命令
litssh run web-server "df -h"
litssh run my-server "docker ps"

# 带外审批(无桌面/headless 时)
litssh approvals            # 列出待审批的敏感操作
litssh approve <审批ID>     # 批准
litssh deny <审批ID>        # 拒绝
```

## 🤝 贡献

欢迎贡献！请提交Issue或Pull Request。架构与开发约定见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

## 📄 许可证

本项目采用 MIT 许可证 - 查看 [LICENSE](LICENSE) 文件了解详情。

## 🔗 相关链接

- [MCP协议](https://modelcontextprotocol.io/)
- [SSH.NET](https://github.com/sshnet/SSH.NET)
- [.NET 8](https://dotnet.microsoft.com/)

## 📧 联系方式

如有问题或建议，请提交Issue。
