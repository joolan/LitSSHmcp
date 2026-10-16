---
name: mysql-troubleshoot
description: MySQL 连接数/慢查询/锁/复制延迟/性能问题排查。适用于「数据库连不上」「Too many connections」「SQL 慢」「表锁住」「主从延迟」。
appliesTo: [datasource, mysql, ssh]
---

# MySQL 排障

## 获取标识
`datasource_list` 拿 `datasourceId`（也可传名称）。用 `mysql_query`（只读）/`mysql_explain`（分析）/`mysql_diagnostics`（体检）/`mysql_execute`（写，需审批）。

## 步骤
1. **体检**：`mysql_diagnostics(datasourceId)` 一次性看连接数、慢查询、锁等待、复制状态概览。
2. **连接数暴涨**：`mysql_query "SHOW PROCESSLIST"` 或查 `information_schema.processlist` 按 user/host/state 聚合；确认是连接泄漏还是业务突增；核对 `max_connections`。
3. **慢查询**：`mysql_query "SHOW GLOBAL STATUS LIKE 'Slow_queries'"`；`mysql_query` 读慢日志表（若开启）；对可疑 SQL 用 `mysql_explain`。
4. **锁等待**：查 `performance_schema.data_lock_waits` / `information_schema.innodb_trx`，找阻塞源与被阻塞会话。
5. **复制延迟**：`SHOW SLAVE STATUS`（或 `SHOW REPLICA STATUS`）看 `Seconds_Behind_Master`、`Slave_IO/SQL_Running`。
6. **磁盘/IO**：结合 `ssh_snapshot_get section="resource"` 看 DB 主机 IO 是否饱和。

## 处置
- 慢 SQL → 加索引/改写，用 `mysql_explain` 验证；写操作走 `mysql_execute`（有审批与过滤器）。
- 锁等待 → 定位长事务，必要时让用户确认后 `KILL <id>`（经 `mysql_execute`）。
- 连接泄漏 → 应用侧连接池配置修正。

## 注意
- 只读查询用 `mysql_query`；任何写/改结构用 `mysql_execute`（无 WHERE 的 UPDATE/DELETE、DROP 等会被拒绝）。
- 结果最多 1000 行；大结果走落盘句柄。凭据不会出现在任何返回里。

## 深入（按需 skill_read）
慢查询深度排查 runbook：`references/slow-query-runbook.md`（慢日志/digest 定位、mysql_explain 归因、处置与验证闭环）。
