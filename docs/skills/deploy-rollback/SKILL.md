---
name: deploy-rollback
description: 发布后验证与回滚检查单（灰度/重启/切流后确认健康，异常时快速回滚）。适用于「刚发布完要确认」「发布后报错要不要回滚」「回滚步骤」。
appliesTo: [ssh, command, service, docker, topology]
---

# 发布后验证与回滚检查单

## 发布前
- 先用 `topology_query(action="search")` 定位应用/服务器，`topology_query(action="impact", assetId=...)` 查变更影响面（谁依赖它、会影响哪些上游），把结果写进验证清单。

## 发布后验证（按顺序，先只读）
1. **进程/容器**：`service_query(action="status", service=unit)` 或 `docker_query(action="ps")` 看目标是否 Running、重启次数是否为 0。
2. **端口/健康**：`ssh_snapshot_get section="portmap"` 或 `ss -lntup | grep <port>`；`curl -sS -m 8 -o /dev/null -w '%{http_code}\n' http://127.0.0.1:<port>/health`（如有健康端点）。
3. **日志**：`log_query(action="tail")`/`service_query(action="logs")`/`docker_query(action="logs")` 看启动后是否报错（异常栈、连接失败、配置缺失）。
4. **依赖**：`app_health_snapshot`(appId) 看运行时连通性；用 `topology_query(action="neighbors"/"path")` 对照依赖链路，结论用 `topology_query(action="explain")` 核实证据与 `stale`。
5. **指标**：`ssh_snapshot_get section="resource"` 看 CPU/内存/负载是否异常升高。

## 判断是否回滚
- 健康检查失败、启动即崩、错误日志持续增长、关键依赖不可用 → 倾向**回滚**。
- 仅少量非关键告警 → 可先观察并修复。

## 回滚（需用户明确要求 + 审批）
1. 记录现场：保留当前版本号/镜像 tag、报错日志片段。
2. 回滚方式取决于发布方式：
   - systemd/二进制/脚本 → 切回上一版本产物后 `systemctl restart`（`ssh_execute_sudo`）。
   - 容器 → `docker` 起回上一个镜像 tag（`docker restart`/compose，需确认）。
   - 配置类 → 恢复上一份配置后 reload。
3. 回滚后**重跑验证清单**，确认恢复正常。

## 注意
- 发布/回滚均为变更操作：遵循"先只读取证 → 最小变更 → 变更后验证"，并说明影响与回滚点。
- 涉及重启/切版本前，确认无正在进行的批量任务。

## 深入（按需 skill_read）
CI/CD 快速回滚 runbook：`references/ci-cd-rollback-runbook.md`（决策门槛、按发布形态回滚对照表、回滚后验证清单）。
