---
name: network-connectivity
description: 排查到目标主机/端口的网络连通性（ping/telnet/curl 不通、DNS、路由、防火墙）。适用于「连不上数据库/接口」「超时」「丢包」「域名解析失败」。
appliesTo: [ssh, command, topology]
---

# 网络连通性排查

## 步骤
0. **先看图谱**：`topology_search` 找源/目标资产，`topology_path(源, 目标)` 看预期链路（跳板/隧道/直连）；`topology_explain` 核对关系是否 `stale`，避免按过期依赖排查。
1. **分层探测**（从本机或跳板机）：
   - ICMP：`ping -c 4 <host>`（有限次数，避免挂起）。
   - 端口：`timeout 5 bash -c '</dev/tcp/<host>/<port>' && echo open || echo closed`（勿用交互式 `telnet`/`nc`）。
   - 应用层：`curl -sS -m 8 -o /dev/null -w '%{http_code} %{time_total}s\n' http://<host>:<port>/`。
2. **DNS**：`getent hosts <name>` / `dig +short <name>`；解析失败或解析到旧 IP → 记录。
3. **路由/丢包**：`traceroute <host>` 或 `mtr -rwc10 <host>`（限次数）。
4. **本地监听/防火墙**：`ss -lntup` 看目标服务是否监听；目标机 `iptables -L -n`/`firewall-cmd --list-all`（需 sudo）。
5. **比对**：与应用日志/配置里的连接串（host:port）比对，确认连的是哪个实例。

## 处置
- DNS/解析问题 → 修 hosts/DNS 或改配置用 IP。
- 防火墙/安全组 → 放行端口（需确认）。
- 目标服务未监听 → 回到服务/容器排障。
- 变更后复测第 1 步三项探测。

## 注意
- 严禁 `ping`/`nc`/`telnet` 无界运行（会挂起）；一律限次数/加 `timeout`。
- 远程执行经 `ssh_execute_command`；改防火墙需 `ssh_execute_sudo` + 确认。
