---
name: script-deploy-verify
description: 远端脚本的生成→上传→执行→验证→清理闭环（含解释器兼容性探测、scripts/ 复用台账、脚本永不免审的安全约束）。适用于「写个脚本传上去跑」「批量处理」「一次性运维脚本」「脚本兼容性」。
appliesTo: [ssh, command, fileTransfer, memory]
---

# 远端脚本：生成 → 上传 → 执行 → 验证 → 清理

## 0. 先查复用（省一次上传+审批）
1. 读工作区 `scripts/MANIFEST.md` 与 `scripts/remote/`：目标机已有等价脚本则直接执行。
2. 看自动召回的相关记忆（每轮注入，格式 `[kind:key ×freq] 内容`），或 `ops_memory_list(kind="script"|"task")`：历史脚本与已验证执行路径；`superseded` 条目表示有更新版本。
3. 都没有再新写；写完准备登记（第 6 步）。

## 1. 生成脚本（写在工作区，不直接塞长命令）
- 落盘到工作区 `scripts/local/<name>.sh`（或 `.py`），单文件、无交互、幂等（重复执行结果一致）。
- **解释器声明第一行**：`#!/usr/bin/env bash`（或 `#!/usr/bin/env python3`）。
- **用 `set -euo pipefail`（bash）/ `set -eu`（sh）**，失败即停，避免半执行状态。
- 脚本自己做验证与输出（见第 4 步），把关键结论打到 stdout，退出码非 0 = 失败。

## 2. 兼容性探测（执行前一次只读命令搞定）
```sh
command -v python3; python3 --version 2>&1 | head -1; command -v python2.7; command -v python;
bash --version | head -1; ls -l /bin/sh; sh --version 2>&1 | head -1 || true
```
- 无 `python3` 才退回 `python2.7`（避免 f-string/`print()` 语义差异：py2 脚本用 `print(...)` 兼容写法）。
- `/bin/sh` 若指向 dash（Debian/Ubuntu）→ **禁止 bashism**（`[[ ]]`、数组、`source`、`echo -e`），用 `sh` 脚本 + POSIX 语法；或明确用 `bash x.sh` 执行。
- 目标机缺解释器 → 改用 POSIX `sh` 重写，不要试图装包（需用户确认的变更另说）。

## 3. 上传（ssh_upload_file，需审批）
- `ssh_upload_file(serverId, localPath="scripts/local/x.sh", remotePath="/tmp/x.sh")`（须在 `allowedRemotePaths` 白名单内）。
- **脚本永不免审**：上传时服务端会扫描内容并把报告（结论/命中行/sha256）放进审批弹窗；命中危险模式直接 `status=blocked_script`。
- 被拦 = 规则判定真的危险：**改写脚本消除风险点**，不要改扩展名/拆分命令绕过检测。

## 4. 执行（ssh_execute_command，需审批）
- `ssh_execute_command(serverId, "bash /tmp/x.sh", timeoutSeconds=300)`。
- 执行侧同样有脚本门：先 `cat` 预读远端内容重新扫描，报告附审批弹窗，**脚本形态一律人工同意**（即使命令行本身不敏感）。
- `riskNote` 可带上助手的评估摘要（标注"仅供参考"，服务端扫描才是权威）。
- 长任务：脚本内部自行控制输出量；不要用前台挂起方式。

## 5. 验证（先只读，紧跟执行）
1. 退出码/输出：脚本已打印 `OK: <关键指标>` 最好。
2. `ssh_snapshot_get section="portmap"` / `ss -lntp | grep <port>`：端口还在/新起。
3. `service_logs` / `log_tail` / `docker_logs`：执行后无新报错。
4. 数值类操作：执行前后各取一次 `df -h`/`count(*)` 对比，确认符合预期。

## 6. 清理 + 沉淀
- 清理远端临时脚本：`ssh_execute_command(serverId, "rm -f /tmp/x.sh")`（`rm` 属敏感，照常审批；或用户要求保留现场）。
- 登记复用资产：`ops_script_register(path="scripts/local/x.sh", summary="用途与验证结论", compat="bash4+ / POSIX sh / python3", deployedTo="ssh:web01", verified=true)` → 写 `scripts/MANIFEST.md` 并同步 `kind=script` 记忆（含已部署资产）。
- 任务复盘（成功后才记）：`ops_memory_save(kind="task", key=<一句话路径>, content=验证通过的执行方式+关键输出特征)`；同一路径改进时用 `ops_memory_update(status="superseded")` 指向新记录。

## 安全约束（不可协商）
- **脚本永不免审**：上传扫描 + 执行前预读扫描 + 审批弹窗展示，三者都不因"脚本是我刚生成的"而跳过。
- Blocked = 硬拒绝，不进审批：按报告逐行修掉危险模式后重新走流程。
- 只读探测（`cat`/探测命令）会写审计（`category=probe`），执行写 `category=exec`，拒绝写 `category=gate`——用户可全程回放。
- 白名单优先：脚本放 `/tmp` 或 `allowedRemotePaths` 内，不写系统目录。
