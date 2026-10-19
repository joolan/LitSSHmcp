---
name: port-process
description: 定位端口占用、进程与监听关系（谁占了 80/443、LISTEN 的进程是谁、端口不通）。适用于「端口被占用」「address already in use」「找不到监听进程」「服务没起来但没报错」。
appliesTo: [ssh, command, topology]
---

# 端口占用与进程定位

## 步骤
1. **整体端口↔进程↔服务**：优先 `ssh_snapshot_get section="portmap"`（端口/进程名/PID/用户/systemd 服务三元组，含程序路径 `exe` 与完整启动命令 `cmdline`）。也可用 `topology_search("<端口>")` 看图谱里已知的应用/数据源端口，再用 `topology_neighbors` 确认归属。
2. **查指定端口**：`ssh_execute_command "ss -lntup | grep ':<port>'"`（tcp+udp，含 PID）；更全用 `lsof -i :<port>`。
3. **从进程反查端口**：`ss -lntup | grep <pid>` 或 `lsof -p <pid> | grep LISTEN`。
4. **确认是否被占用**：若服务起不来且日志报 `address already in use`，用第 2 步找出占用者（可能是残留进程或另一个实例）。
5. **区分监听地址**：`127.0.0.1:port` 只本机、`0.0.0.0:port`/`[::]:port` 对外；端口不通先核对监听地址与防火墙。

## 处置
- 残留进程占用 → 确认后停掉（`kill <pid>` 属敏感操作，经 `ssh_execute_sudo`，需确认）。
- 端口未监听 → 看服务状态（`service_status`/`docker_ps`）与启动日志，定位启动失败原因。
- 改端口/配置 → 变更后复测 `ss -lntup | grep`。

## 注意
- `kill`/`pkill` 需用户确认；不要 `kill -9` 前先确认进程归属与依赖（是业务进程还是残留）。
- 优先只读：`ss`/`lsof`/快照 portmap。
