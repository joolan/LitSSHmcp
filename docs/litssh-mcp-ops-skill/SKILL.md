---
name: litssh-mcp-ops-skill
description: Use when operating or troubleshooting servers and applications through the LitSSH MCP server (LitSSHmcp) — SSH command execution, Docker containers, systemd services, Java/JVM diagnostics, log files, MySQL/PostgreSQL/Redis, topology and app health. Triggers on tools like ssh_execute_command, docker_ps, service_status, java_threads, log_tail/log_grep/log_find, app_health_snapshot, mysql_diagnostics, and on requests such as 服务器排查, 应用日志, 容器起不来, JVM/CPU 飙高, 数据库连不上. Also gives SSH-based workarounds for capabilities the MCP does not expose directly.
---

# LitSSH MCP 服务器运维排障

本 skill 指导 AI 智能体通过 **LitSSH MCP**（`LitSSHmcp.McpServer`，stdio）对多台服务器、Java 应用、Docker、systemd、日志、MySQL/PostgreSQL/Redis 做排查与运维。

- MCP 服务进程**自己发起 SSH** 到目标机执行命令；桌面 App 只用于编辑 `config.json`，不需要常驻。
- 每个工具都是**单目标**：传一个 `serverId`/`datasourceId`（或 `appId`）。多台机器 = 多次调用。
- 工具有**分组开关**（`tools.enabledGroups`）：某类工具可能未启用，此时用 `ssh_execute_command` 变通（见第 6 节）。

## 0. 开始前

1. 不确定有哪些工具 / 怎么用 → 调 `mcp_usage_guide`；MCP 自身异常 → `mcp_self_check`。
2. 拿到准确的标识：
   - 服务器：`ssh_list_servers` → 取 `id`（用 `id` 最稳，也可传名称/主机名）。
   - 数据源：`datasource_list` → 取 `id`。
   - 应用：`topology_get_overview` 或应用管理；`app_health_snapshot` 接受应用 ID 或名称。
3. **不要猜 ID**：传错时错误信息会回显可用 ID；名称/主机名命中多台会返回 `server_ambiguous`，改用精确 `id`。

## 1. 安全与返回约定（必读）

- 敏感命令（`rm/chmod/systemctl restart/docker run|exec|restart` 等）会触发**人工审批**，可能返回：
  - `rejected`（人工拒绝，**不要重试**）
  - `approval_timeout`（超时，提示用户后可用同一操作重试）
  - `approval_unavailable`（无可用审批通道）
- 危险命令返回 `blocked`（不会执行，**不要改写绕过**）。
- 结果统一含 `success` + `status` + `error`，并回显 `serverId/serverName/host`。常见 `status`：
  `blocked` / `rejected` / `approval_timeout` / `server_not_found` / `server_ambiguous` / `datasource_not_found` / `datasource_ambiguous` / `app_not_found` / `path_not_allowed` / `readonly_statement` / `not_readonly_statement` / `auth_failed` / `host_key_mismatch` / `timeout` / `rate_limited` / `connection_error` / `pid_not_found` / `pid_ambiguous` / `log_path_ambiguous`。
- `host_key_mismatch` 可能是安全事件：先人工核对 SSH 指纹，**不要**自动忽略。
- 命令输出超过 2 万字符会截断（`truncated: true`）；用更精确的条件重查。
- **审计按会话/工具区分**：每次启动 MCP 服务会生成一个 `sessionId`，所有命令/SQL 审计记录都带它，并带 `tool`（产生记录的 MCP 工具名，如 `docker_logs`/`mysql_query`）；`sessionId` 与 `tool` 都参与哈希链防篡改。`mcp_self_check` 返回当前 `sessionId` 与 `clientName`/`clientVersion`；`mcp_list_sessions` 列出最近会话（哪个客户端/哪次连接）及首末活动时间。排查"某次会话/某个客户端/某个工具做了什么"时，用 `ssh_get_command_history` / `datasource_get_sql_history` 的 `sessionId=` / `tool=` 过滤。

## 2. 工具分组速查

| 意图 | 工具 |
|---|---|
| 服务器列表/状态/连通性 | `ssh_list_servers`、`ssh_get_server_status`、`ssh_test_connection` |
| 执行命令 / 提权 / 历史 | `ssh_execute_command`、`ssh_execute_sudo`、`ssh_get_command_history`、`ssh_get_sudo_status` |
| 文件 | `ssh_list_files`、`ssh_upload_file`、`ssh_download_file` |
| Docker | `docker_ps`、`docker_logs`、`docker_inspect`、`docker_stats`、`docker_images`、`docker_restart`、`docker_exec` |
| systemd | `service_status`、`service_list`、`service_restart`、`service_logs` |
| 日志文件 | `log_find`（发现）、`log_tail`、`log_grep` |
| JVM | `java_processes`、`java_threads`、`java_heap`、`java_info` |
| 拓扑 | `topology_get_overview`、`topology_get_dependencies`、`topology_discover` |
| 应用体检 | `app_health_snapshot` |
| 数据源 | `datasource_list`、`datasource_test_connection`、`datasource_get_sql_history` |
| MySQL/PG/Redis | `mysql_*` / `postgres_*` / `redis_*`（query/execute/explain/diagnostics；Redis 为 read/execute/diagnostics） |

## 3. 标准排障流程（Triage）

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

- **容器起不来/异常退出**：`docker_ps(all=true)` → `docker_logs(container, tail=300)` → `docker_inspect`（看退出码/挂载/健康检查）→ `docker_stats`。
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

**6.1 定位/读取日志（MCP 没覆盖的路径）**
```
# 看启动参数找日志配置
ssh_execute_command(serverId, "ps -eo pid,args --no-headers | grep '[j]ava' | head")
# 找配置文件
ssh_execute_command(serverId, "find /opt/order -maxdepth 3 \\( -name 'application*.yml' -o -name 'application*.properties' -o -name 'logback*.xml' -o -name 'log4j2*.xml' \\) 2>/dev/null")
# 读配置里的日志路径
ssh_execute_command(serverId, "grep -nE 'logging\\.(file|path)|log\\.path|LOG_PATH' /opt/order/config/application.yml 2>/dev/null")
# 直接读日志（若 log_tail 白名单不包含该路径，可用 tail 变通；仍受命令过滤）
ssh_execute_command(serverId, "tail -n 200 /opt/order/logs/app.log")
# 按关键字检索
ssh_execute_command(serverId, "grep -n -i -- 'OutOfMemory\\|Exception' /opt/order/logs/app.log | tail -n 100")
```
> 若路径应长期使用，建议把该目录加入 `security.logs.allowedPaths`（安全设置窗口），或在该应用填「日志路径」，之后就能用 `log_tail`/`log_grep`。

**6.2 端口/进程/资源**
```
ssh_execute_command(serverId, "ss -ltnp 2>/dev/null | grep -E ':8080|:80' || netstat -ltnp")
ssh_execute_command(serverId, "ps -eo pid,ppid,pcpu,pmem,etime,args --sort=-pcpu | head -20")
ssh_execute_command(serverId, "free -m; echo ---; df -h; echo ---; df -i")
ssh_execute_command(serverId, "du -sh /var/log/* 2>/dev/null | sort -h | tail -20")
```

**6.3 HTTP/健康检查/依赖探测**
```
ssh_execute_command(serverId, "curl -s -m 5 http://127.0.0.1:8080/actuator/health")
ssh_execute_command(serverId, "curl -s -m 5 -o /dev/null -w '%{http_code}\\n' http://127.0.0.1:8080/")
ssh_execute_command(serverId, "timeout 3 bash -c 'cat < /dev/null > /dev/tcp/10.0.0.9/3306' && echo open || echo closed")
```

**6.4 OOM / 内核 / 重启痕迹（可能需要 sudo）**
```
ssh_execute_sudo(serverId, "dmesg -T | grep -iE 'oom|killed process' | tail -30")
ssh_execute_sudo(serverId, "journalctl -k --since '2 hours ago' | grep -i oom")
ssh_execute_sudo(serverId, "last reboot | head")
```

**6.5 领域工具未启用时的替代**
- 无 `docker_*`：`ssh_execute_command(serverId, "docker ps -a")` / `"docker logs --tail 200 <c>"` / `"docker stats --no-stream"`。
- 无 `service_*`：`"systemctl status <s> --no-pager"` / `"journalctl -u <s> -n 200 --no-pager"`；重启用 `ssh_execute_sudo(serverId, "systemctl restart <s>")`。
- 无 `java_*`：`"jstack -l <pid>"` / `"jcmd <pid> GC.heap_info"` / `"jstat -gcutil <pid> 1000 1"`。

**6.6 远端配置修改（高风险，务必谨慎）**
MCP 没有“编辑远端文件”工具。稳妥做法：
1. `ssh_download_file` 拉取配置到本地 → 本地修改；
2. `ssh_upload_file` 回传（需审批）；
3. `service_restart` / `ssh_execute_sudo systemctl reload`（需审批）；
4. 校验：`curl /actuator/health` 或看日志。
避免用 `sed -i` 之类直接改生产配置，除非别无选择且已有备份。

**6.7 其它环境/编排**（MCP 未内置）
- Kubernetes：`ssh_execute_command(serverId, "kubectl get pods -A -o wide")`、`"kubectl describe pod <p> -n <ns>"`、`"kubectl logs <p> -n <ns> --tail=200"`。
- Nginx：`"nginx -t"`、`"tail -n 200 /var/log/nginx/error.log"`。
- 消息队列/中间件：优先用其自带 CLI（`kafka-*`、`rabbitmqctl`）经 `ssh_execute_command`。
- 需要长期指标/追踪（Prometheus/APM）时，MCP 未覆盖，改用对应系统或经 ssh 拉取。

## 7. 输出与协作规范

- 先给**证据**再给结论；标注证据来自哪个工具 + 哪台 `host`。
- 只读优先；任何“重启/删除/改配置”都先说明影响面并等待确认（会触发审批）。
- 遇 `blocked`/`rejected` 不要重试轰炸；遇 `approval_timeout` 提示用户后重试；遇 `server_ambiguous`/`pid_ambiguous`/`log_path_ambiguous` 用精确标识重试。
- 不要自行编造路径、端口、ID；拿不到就用列表/发现工具（`ssh_list_servers`、`log_find`、`java_processes`）。

## 8. 参考

- 工具权威说明：仓库 `docs/TOOLS.md`（如已挂载为 reference 可直接读取；也可调 `mcp_usage_guide`）。
- MCP 自身配置问题：`mcp_self_check`；主机密钥/连通性：`ssh_test_connection`。
