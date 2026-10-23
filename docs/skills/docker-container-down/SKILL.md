---
name: docker-container-down
description: Docker 容器起不来 / 反复重启 / 退出 / 不健康排查。适用于「容器 Exited」「Restarting」「ImagePullBackOff 类」「容器启动后立刻退出」。
appliesTo: [docker, ssh, command, log]
---

# 容器起不来排查

## 步骤
1. **现状**：`docker_query(action="ps", serverId=..., all=true)` 看状态（Up/Exited/Restarting）与重启计数。
2. **看日志**：`docker_query(action="logs", container=..., tail=300)`——最常见的根因就在启动日志里（配置错、端口占用、依赖未就绪、权限）。
3. **配置/环境**：`docker_query(action="inspect", container=...)` 看 `Config.Env`、`Entrypoint`、挂载、端口映射、`State.ExitCode`/`OOMKilled`。
4. **退出码**：`137`=被 OOM/强杀；`1`=应用错误；`126/127`=命令/入口不可执行；对照日志定位。
5. **资源**：`docker_query(action="stats")` 看 CPU/内存是否触顶；`ssh_snapshot_get section="resource"` 看宿主。
6. **端口冲突**：`ssh_snapshot_get section="portmap"` 或 `ss -lntp` 确认端口是否被占用。

## 处置
- 配置/环境问题：修正 compose/`docker run` 参数后重建（`docker_restart`/`docker_exec` 均需确认）。
- 依赖未就绪：加启动重试/健康检查。
- OOM：调大内存限制或修内存问题。
- 镜像拉取失败（离线环境）→ 见 MCP 未覆盖能力用 SSH 变通。

## 注意
- `docker restart/rm/rmi/run/exec/stop` 等均需桌面确认；`docker system prune` 等危险命令被拒。
- 优先只读（`docker_query` 的 ps/logs/inspect/stats），确需变更再申请审批。
