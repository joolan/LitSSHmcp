---
name: service-failure
description: systemd 服务启动失败 / 反复重启 / 不健康排查。适用于「service failed」「服务起不来」「Active: failed」「journalctl 报错」。
appliesTo: [service, ssh, command, log]
---

# systemd 服务失败排查

## 步骤
1. **状态**：`service_list(serverId)` 找失败/异常的单元；`service_status(unit)` 看 `Active`、`Main PID`、退出码。
2. **日志（关键）**：`service_logs(unit, lines=200)`（journalctl 包装）看首次报错的根因；也可 `ssh_execute_sudo "journalctl -u <unit> -n 200 --no-pager"`。
3. **配置校验**：`ssh_execute_sudo "systemctl cat <unit>"` 看 unit 文件；`systemd-analyze verify <unit>`。
4. **环境**：`docker_*` 若服务依赖容器；`ssh_snapshot_get section="portmap"` 看端口占用；`section="resource"` 看 OOM/磁盘。
5. **权限/路径**：`WorkingDirectory`、`User`、可执行文件路径与权限（对照日志中的 `Permission denied`/`No such file`）。

## 处置
- 配置错误：修正 unit（`systemctl edit`/改文件）后 `systemctl daemon-reload && systemctl restart <unit>`（`ssh_execute_sudo`，需确认）。
- 端口占用：先停冲突进程或改端口。
- 依赖未就绪：加 `After=`/`Wants=` 或重试逻辑。

## 注意
- 重启服务、改配置属变更操作，需用户明确要求 + 审批。
- 先只读取证（status/logs/cat），定位根因后再动。
