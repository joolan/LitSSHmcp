---
name: litssh-mcp-ops-skill
description: Use when operating or troubleshooting servers and applications through the LitSSH MCP server (LitSSHmcp) — SSH command execution, server snapshots, Docker containers, systemd services, Java/JVM diagnostics, log files, MySQL/PostgreSQL/Redis, topology and app health. Triggers on tools like ssh_execute_command, ssh_snapshot_get/ssh_snapshot_refresh, docker_ps, service_status, java_threads, log_tail/log_grep/log_find, app_health_snapshot, mysql_diagnostics, and on requests such as 服务器排查, 整机态势/快照, 应用日志, 容器起不来, JVM/CPU 飙高, 数据库连不上. Also gives SSH-based workarounds for capabilities the MCP does not expose directly. Maintains an ops asset/app-topology ledger (OPS_ASSETS.md) in the workspace — creating and correcting it while operating so servers/apps/log paths/dependencies stay accurate.
---

# LitSSH MCP 服务器运维排障

本 skill 指导 AI 智能体通过 **LitSSH MCP**（`LitSSHmcp.McpServer`，stdio）对多台服务器、Java 应用、Docker、systemd、日志、MySQL/PostgreSQL/Redis 做排查与运维。

- MCP 服务进程**自己发起 SSH** 到目标机执行命令；桌面 App 只用于编辑 `config.json`，不需要常驻。
- 每个工具都是**单目标**：传一个 `serverId`/`datasourceId`（或 `appId`）。多台机器 = 多次调用。
- 工具有**分组开关**（`tools.enabledGroups`）：某类工具可能未启用，此时用 `ssh_execute_command` 变通（见第 6 节）。

## 0. 开始前

0. **资产档案按需读取**：需要既有的服务器/应用/日志路径/拓扑信息时，才读工作区 `OPS_ASSETS.md`（见第 9 节）；本次确认/纠正了新事实再**增量更新**，**不必每轮都读写**。
1. 不确定有哪些工具 / 怎么用 → 调 `mcp_usage_guide`；MCP 自身异常 → `mcp_self_check`。
2. 拿到准确的标识：
   - 服务器：`ssh_list_servers` → 取 `id`（用 `id` 最稳，也可传名称/主机名）。
   - 数据源：`datasource_list` → 取 `id`。
   - 应用：`topology_get_overview` 或应用管理；`app_health_snapshot` 接受应用 ID 或名称。
3. **不要猜 ID**：传错时错误信息会回显可用 ID；名称/主机名命中多台会返回 `server_ambiguous`，改用精确 `id`。

## 1. 安全与返回约定（必读）

- 敏感命令（`rm/chmod/systemctl restart/docker run|exec|restart` 等）会触发**人工审批**，可能返回：`rejected`（人工拒绝，**不要重试**）、`approval_timeout`（超时，提示用户后可重试）、`approval_unavailable`（无可用审批通道）。
- 危险命令返回 `blocked`（**不会执行，不要改写绕过**）。
- 审批模式（`security.approval.mode`）可被设为 `auto-approve` / `auto-reject`：若返回 `rejected` 且提示“审批模式(自动拒绝)”，说明是服务器配置，**本次会话不要反复重试**。
- 结果统一含 `success` + `status` + `error`，并回显 `serverId/serverName/host`。常见 `status`：`blocked` / `rejected` / `approval_timeout` / `approval_unavailable` / `server_not_found` / `server_ambiguous` / `server_disabled` / `datasource_not_found` / `datasource_ambiguous` / `app_not_found` / `path_not_allowed` / `readonly_statement` / `not_readonly_statement` / `auth_failed` / `host_key_mismatch` / `timeout` / `rate_limited` / `connection_error` / `pid_not_found` / `pid_ambiguous` / `log_path_ambiguous`。
- `host_key_mismatch` 可能是安全事件：先人工核对 SSH 指纹，**不要**自动忽略。
- 命令输出超过 2 万字符会截断（`truncated: true`）；用更精确的条件重查。
- **审计按会话/工具区分**：`mcp_self_check` 返回当前 `sessionId` 与客户端信息；`mcp_list_sessions` 列出最近会话；`ssh_get_command_history` / `datasource_get_sql_history` 支持 `sessionId=` / `tool=` 过滤。
- 任何工具的入参/出参都**不含密码**；提权命令的 `output`/`error` 中口令一律脱敏为 `******`。

## 2. 工具分组速查

| 意图 | 工具 |
|---|---|
| 服务器列表/状态/连通性 | `ssh_list_servers`、`ssh_get_server_status`、`ssh_test_connection`（已禁用的服务器不在列表中，相关工具会返回 `server_disabled`） |
| 服务器整机快照 | `ssh_snapshot_get`（默认读本地最新快照，返回各维度概览；`section=`/`detail="full"` 取完整）、`ssh_snapshot_refresh`（重新采集，较慢） |
| 执行命令 / 提权 / 历史 | `ssh_execute_command`、`ssh_execute_sudo`、`ssh_get_command_history`、`ssh_get_sudo_status` |
| 文件 | `ssh_list_files`、`ssh_upload_file`、`ssh_upload_files`（批量/目录，一条连接）、`ssh_download_file`、`ssh_download_files`（批量/目录，一条连接） |
| Docker | `docker_ps`、`docker_logs`、`docker_inspect`、`docker_stats`、`docker_images`、`docker_restart`、`docker_exec` |
| systemd | `service_status`、`service_list`、`service_restart`、`service_logs` |
| 日志文件 | `log_find`（发现）、`log_tail`、`log_grep` |
| JVM | `java_processes`、`java_threads`、`java_heap`、`java_info` |
| 拓扑 | `topology_get_overview`、`topology_get_dependencies`、`topology_discover` |
| 应用体检 | `app_health_snapshot` |
| 数据源 | `datasource_list`、`datasource_test_connection`、`datasource_get_sql_history` |
| MySQL/PG/Redis | `mysql_*` / `postgres_*` / `redis_*`（query/execute/explain/diagnostics；Redis 为 read/execute/diagnostics） |

### 服务器快照（整机态势，优先用）

`ssh_snapshot_get(serverId)` 拿到该服务器**整机态势概览**（本机持久化，默认只读本地、不连服务器）。**默认只回各维度概览（关键指标 + 数据大小），不含完整数据**：需要某维度完整数据用 `section="resource|portmap|docker|nginx_tls|systemd|security"`，需要全部才用 `detail="full"`（较大）。无快照或要最新数据用 `ssh_snapshot_refresh`（较慢、同机单飞，参数同上）。

- `resource`：CPU/内存/磁盘/负载/系统信息；含**阈值告警 `warnings`/`riskLevel`**（内存/swap/磁盘/负载）与 **top5 进程 `topByCpu`/`topByMemory`**（谁在吃 CPU/内存）。
- `portmap`：端口↔进程名/PID/用户↔systemd 服务三元组，含**程序路径 `exe`** 与**完整启动命令 `cmdline`**（凭据参数已脱敏），区分 TCP/UDP、双栈；Unix socket 分系统级(`unixSockets`)与**桌面/用户会话(`unixSocketsDesktop`，默认折叠**，如 gnome/pipewire/dbus/X11)。
- `docker`：Docker 守护进程概览（版本/容器与镜像数/存储驱动）+ 容器清单（名称/镜像/状态/端口）+ 运行容器资源（CPU/内存/网络/块IO/PIDs）；未装 docker 该维度 `skipped`。
- `nginx_tls`：**完整有效配置 `effectiveConfig`**（`nginx -T` 展开 include）+ **域名列表 `domains`**（每个域名是否 `ssl`、端口、关联证书到期/SAN）；未装 nginx 该维度 `skipped`。
- `systemd`：单元健康聚合与失败清单。
- `security`：安全巡检——SSH 有效配置（`PermitRootLogin`/`PasswordAuthentication`/端口）、防火墙（`ufw`/`firewalld`/iptables）、`fail2ban`、MySQL 匿名账户/远程 root/可远程登录的高权账户（`SHOW GRANTS` 判定；**优先用"匹配该服务器的已配置 MySQL 数据源凭据"核查，账号需有 `mysql.*` SELECT 权限；否则标注未检查**）、系统空口令账户、sudoers `NOPASSWD`，以及**公网暴露的高危端口**（3306/5432/6379/2375/9200/21/23…）；统一输出 `findings[]`（`severity`/`id`/`title`/`detail`/`evidence`）与 `summary` 计数。

维度状态：`ok`/`degraded`（如未提权或缺权限）/`skipped`（环境不具备）/`failed`；返回还含 `events`（采集事件流水）与 `recent`（最近若干份摘要）。**整机盘点优先用快照**，比逐条拼 `ssh_execute_command` 全面且稳定。**`ssh_snapshot_refresh` 有最短刷新间隔节流（默认 60s，超过会返回 `status=fresh` 的缓存，除非传 `force=true`）——同一任务内不要重复刷新，先 `get`。**

## 3. 标准排障流程（Triage）

0. **整机态势**：先 `ssh_snapshot_get(serverId)`（默认概览）；仅当**无快照**或**确需最新**时 `ssh_snapshot_refresh(serverId)`（较慢、同机单飞、**有最短刷新间隔节流，不要在同一任务内重复刷新**）。需要某维度细节用 `section=`，全部才用 `detail="full"`。
1. **定位**：`app_health_snapshot(appId)` 一步拿到该应用所在服务器的 Java 进程/容器/监听端口 + 依赖库连通性；或 `topology_get_overview` / `topology_get_dependencies` 看依赖。
2. **分侧取证**：
   - 服务器：`ssh_get_server_status` / `ssh_test_connection`（失败会给 `auth_failed`/`host_key_mismatch`/`timeout`/`connection_error`）。
   - 应用进程：`java_processes`（可传 `appId`）→ `java_threads` / `java_heap` / `java_info`（可传 `appId` 自动解析 pid）。
   - 容器：`docker_ps` → `docker_logs` / `docker_inspect` / `docker_stats`。
   - systemd：`service_status` → `service_logs`。
   - 日志：`log_find` → `log_tail` / `log_grep`。
   - 数据库：`mysql_diagnostics` / `postgres_diagnostics` / `redis_diagnostics` → 下钻 `*_query`。
3. **给结论**：输出 时间线 / 根因假设 / 证据（工具+关键输出）/ 处置建议 / 是否需要变更审批。

## 4. 常见场景处方

- **整机盘点 / 无明显方向**：先 `ssh_snapshot_get(serverId)` 看 资源/端口进程服务/Docker/nginx 证书/systemd 全貌，再按异常下钻（比逐条 `ssh_execute_command` 全面稳定）。
- **安全巡检 / 合规核查**：`ssh_snapshot_get(serverId)` 的 `security` 维度一键给出：SSH 配置（root 登录/密码登录/端口）、防火墙（ufw/firewalld）、fail2ban、MySQL 匿名账户/远程 root、系统空口令账户、sudoers `NOPASSWD`、**公网暴露的高危端口**；按 `findings[].severity`（high/medium/low/info）汇报并可给出整改建议；需最新数据用 `ssh_snapshot_refresh`。
- **容器起不来/异常退出**：`docker_ps(all=true)` → `docker_logs(container, tail=300)` → `docker_inspect`（看退出码/挂载/健康检查）→ `docker_stats`；不知哪些容器先 `ssh_snapshot_get` 看 `docker.containers`。
- **Java CPU 飙高/卡死**：`java_processes` 找 pid → `java_threads`（找 `RUNNABLE`/死锁）→ `java_heap`（GC/堆）→ `java_info`。
- **服务没起来**：`service_status`（看退出码/最近日志）→ `service_logs`；需要时 `service_restart`（审批）。
- **数据库慢/连接暴涨**：先 `mysql_diagnostics` / `postgres_diagnostics`（整体），再 `mysql_query` 查 `processlist`/慢日志，`mysql_explain` 分析 SQL；缓存问题用 `redis_diagnostics` + `redis_read`。
- **连不上数据库**：`datasource_test_connection`（区分直连/经隧道），再看绑定的 SSH 隧道服务器是否可用。
- **日志排查**：不知道路径先 `log_find`；已知路径 `log_tail`/`log_grep`；容器用 `docker_logs`；systemd 用 `service_logs`。

## 5. 日志路径怎么找（重点）

`log_tail`/`log_grep` **不自动发现路径**，需要 `path`（白名单 `security.logs.allowedPaths` 内）或 `appId`（应用管理里配置的「日志路径」）。找不到时按下面顺序：

1. **先发现**：`log_find(serverId)`（或 `log_find(serverId, path="/opt/myapp")`）列出最近修改的 `.log`。
2. **从启动命令推断**：`java_processes(serverId, appId)` 看 `command`：
   - `-Dlogging.file=/...`、`-Dlogging.file.name=/...`
   - `-Dlog4j.configurationFile=`、`-Dlogback.configurationFile=`
   - `--spring.config.location=`、`-Dspring.profiles.active=`
   - jar 所在目录（如 `/opt/order/order.jar` → 常见 `/opt/order/logs/`）
3. **解析配置文件**（用 `ssh_execute_command`，见第 6 节）后 `log_tail`。
4. 配好应用「日志路径」后，后续直接 `log_tail(serverId, appId="order")`。

## 6. MCP 未覆盖能力 → 用 SSH 变通（关键）

当对应领域工具未启用、或 MCP 没有该能力时，用 `ssh_execute_command`（只读排查）或 `ssh_execute_sudo`（需提权，触发审批）实现。**先只读、后写入；写入前先拿到证据并征得确认。**

> 完整片段（日志/进程/端口/HTTP/OOM/k8s/nginx/MQ、**避免挂起/丢结果**等）见 **`references/ssh-workarounds.md`**。最常用的三条：
> - 进程/资源：`ssh_execute_command(serverId, "ps -eo pid,ppid,pcpu,pmem,etime,args --sort=-pcpu | head -20")`
> - 端口监听：`ssh_execute_command(serverId, "ss -ltnp 2>/dev/null | grep -E ':8080|:80'")`
> - HTTP 健康：`ssh_execute_command(serverId, "curl -s -m 5 http://127.0.0.1:8080/actuator/health")`

## 7. 输出与协作规范

- 先给**证据**再给结论；标注证据来自哪个工具 + 哪台 `host`。
- 只读优先；任何“重启/删除/改配置”都先说明影响面并等待确认（会触发审批）。
- 遇 `blocked`/`rejected` 不要重试轰炸；遇 `approval_timeout` 提示用户后重试；遇 `server_ambiguous`/`pid_ambiguous`/`log_path_ambiguous` 用精确标识重试。
- **减少审批次数**：多个**只读**提权检查（读 sudoers/服务文件/端口属主等）尽量**合并成一条 `ssh_execute_sudo` 命令**一次执行，而不是逐条弹审批。
- 不要自行编造路径、端口、ID；拿不到就用列表/发现工具（`ssh_list_servers`、`log_find`、`java_processes`）。
- 本次如确认/纠正了新事实，收尾时**增量更新资产档案**（`OPS_ASSETS.md`，见第 9 节）：**优先增量追加/定点修正，避免整份重写**；无新事实则跳过。

## 8. 参考

- 工具权威说明：仓库 `docs/TOOLS.md`（如已挂载为 reference 可直接读取；也可调 `mcp_usage_guide`）。
- MCP 自身配置问题：`mcp_self_check`；主机密钥/连通性：`ssh_test_connection`。
- `references/ssh-workarounds.md`：MCP 未覆盖能力的 SSH 变通手册（第 6 节展开）。
- `references/asset-ledger.md`：运维资产档案维护细则（第 9 节展开）；起手模板 `OPS_ASSETS.template.md`。

## 9. 运维资产档案（在工作区创建并持续维护）

**目标**：让资产/拓扑知识“自动进化”。在工作空间（或用户项目根目录）维护一份**独立的资产 + 应用拓扑关系文档**，需要时读取、再用最新事实**增量更新**纠正它，从而越来越准地定位**服务器、应用、日志**。它是 AI 的“长期记忆”，**不是** MCP 的一部分。

- **位置**：默认工作区根目录 `OPS_ASSETS.md`（也可放 `docs/` 或用户指定）；只维护一份；**通过工作区文档能力维护（优先增量更新，避免整份重写、省 token）**。起手模板：`OPS_ASSETS.template.md`。
- **何时做**：需要历史信息时才读；发现新事实或纠正旧事实就**立即增量更新并记变更**；拓扑变动时用 `topology_discover` 对账。
- **内容结构**：服务器 / 数据源 / 应用（含端口、部署路径、**日志路径**）/ 拓扑关系 / 待确认存疑 / 更新日志（表格）；**结构可按实际环境扩展/精简**（如域名证书、Cron、MQ、备份、K8s 等）。
- **核心规则**（细则见 `references/asset-ledger.md`）：① 以 MCP 实时数据为准纠正；② 存稳定的 `id`；③ 只记事实带证据；④ **绝不存密钥**；⑤ 合并去重；⑥ 冲突不硬猜（放“待确认”并问用户）；⑦ 不覆盖用户手写内容；⑧ **可按实际调整文档结构/内容，但必须注明调整原因**（在更新日志以 `类型=结构` 记录：改了什么 + 为什么 + 影响）。
- **文档过大可拆分**：条目/日志很多时，拆成“主索引 `OPS_ASSETS.md` + 子文件”（如 `ops/asset-changelog.md` 单独放更新日志、`ops/asset-apps.md` 等）；主文档保留摘要与相对链接，同一事实只存一处，拆分同样要注明原因。
