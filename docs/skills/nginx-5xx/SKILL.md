---
name: nginx-5xx
description: 排查网站/接口的 nginx 5xx（502/503/504）与网关超时、请求打不开。适用于「502 Bad Gateway」「504 超时」「站点访问报错」。
appliesTo: [ssh, log, command, service, docker, topology]
triggers: [nginx 502, nginx 503, nginx 504, 502 Bad Gateway, 503, 504, 5xx, upstream 超时, nginx 报错, 网关超时, 站点访问报错]
symptoms: [http_5xx, upstream_error, gateway_timeout]
assets: [nginx, app, domain]
---

# nginx 5xx / 网关超时排查

## 目标
定位 5xx 的直接来源（nginx 自身 / 上游 upstream / 后端应用 / 网络），给出最小处置。

## 步骤
1. **确认目标与入口**：`ssh_list_servers` 拿 `id`；不确定 nginx 在哪台机用 `ssh_execute_command` 跑 `ss -lntp | grep -E ':80|:443'`。已知域名/应用名时先用 `topology_query(action="search")` 定位应用，再用 `topology_query(action="path", from=域名, to=应用)` 或 `topology_query(action="neighbors")` 看已知链路（图谱没有记录时以 nginx 配置与快照为准）。
2. **看 nginx 错误日志（关键）**：`log_query(action="grep", path="/var/log/nginx/error.log", pattern="error|upstream|timed out|connect")`，或 `log_query(action="tail")`。关注：
   - `upstream timed out` / `no live upstreams` → 后端慢或全挂。
   - `connect() failed (111: Connection refused)` → 后端进程没监听 / 崩了。
   - `SSL_do_handshake() failed` → 证书/协议问题。
   - `worker_connections are not enough` → 连接数不足。
3. **定位 upstream 指向**：用 `ssh_execute_sudo` 跑 `nginx -T 2>/dev/null | grep -A5 upstream` 或直接看站点配置，拿到后端 host:port。
4. **检查后端进程/端口**：`ssh_snapshot_get section="portmap"` 或 `ssh_execute_command "ss -lntp | grep <port>"`；确认进程存活、端口监听。
5. **后端应用侧**：若是 Java → `java_processes` / `java_diagnostics(action="threads")` 看线程池/死锁；`log_query(action="tail", appId=...)` 看应用错误。若是容器 → `docker_query(action="ps"/"logs")`。
6. **系统级**：`ssh_snapshot_get section="resource"` 看负载/内存/文件句柄；`dmesg`/`journalctl -u nginx` 看 OOM。

## 判定与处置
- 后端全挂 → 先重启/拉起后端（`service_restart` 或容器重启，均需审批），再验证。
- 后端慢 → 看慢查询/线程栈，扩容或限流。
- nginx 自身配置/证书 → 修正后 `nginx -t` 校验再 `nginx -s reload`。
- 变更后：用 `curl -sS -o /dev/null -w "%{http_code}"` 复测，并复核 error.log 不再报错。

## 注意
- nginx 配置/重载属于变更操作，需用户明确要求；`reload` 用 `ssh_execute_sudo`。
- 不要因一条日志就下结论，结合 upstream 与后端两侧证据；图谱结论用 `topology_query(action="explain")` 核对证据与 `stale` 标记。
- 改配置/重启前用 `topology_query(action="impact")` 查影响面，避免只看到一个站点。
