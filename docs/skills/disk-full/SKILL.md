---
name: disk-full
description: 磁盘空间或 inode 满导致写入失败、服务异常。适用于「No space left on device」「磁盘满了」「df 100%」「写不进文件」。
appliesTo: [ssh, command, log]
---

# 磁盘 / inode 满排查

## 步骤
1. **确认分区**：`ssh_execute_command "df -h; df -i"`，找出使用率高的挂载点，注意 `%iused`（inode）。
2. **定位大目录**：从挂载点往下逐层 `du -x -h --max-depth=1 <dir> | sort -rh | head`，优先 `/var/log`、`/var/lib/docker`、`/tmp`、应用日志目录、`/home`。
3. **定位大文件/删除但仍被占用的文件**：`lsof +L1 2>/dev/null | sort -k7 -rn | head`（进程删除但句柄未释放，是常见“df 满但 du 不大”的元凶）。
4. **inode 满**：`find <dir> -xdev -type f | wc -l` 或按目录统计小文件（session/cache 目录常见）。
5. **日志/临时文件**：用 `log_find` 找大日志；确认为可清理的旧日志/临时文件。

## 处置（需用户确认）
- 清理可安全删除的旧日志（如 `find /var/log -name '*.gz' -mtime +30 -delete`，建议先列出）。
- 被占用的大文件：重启对应进程释放句柄，或 `truncate -s 0 <file>`（确认是关键日志再用）。
- 配置日志轮转（logrotate）或增长上限，防止复发。
- 清理后复测 `df -h`。

## 注意
- 删除文件属破坏性操作：先列清单让用户确认，禁止 `rm -rf` 通配大目录。
- 不要动 `/proc`、系统关键文件；`/var/lib/docker` 清理优先用 docker 原生命令（`docker system prune` 等，需确认）。

## 深入（按需 skill_read）
磁盘周期巡检 runbook：`references/inspection-runbook.md`（批量采集、阈值分级报告、巡检对比与预警）。
