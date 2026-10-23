# MySQL 慢查询深度排查 runbook（按需读取）

适用：业务反馈「SQL 变慢」「数据库拖垮应用」，需要从现象定位到具体 SQL 与根因。
全程优先只读（`mysql_query` 的 `action=query/explain/diagnostics`）；改库/杀连接走 `mysql_execute`（审批）。

## 1. 确认现象与影响面
1. `mysql_query(action="diagnostics", datasourceId=...)`：看连接数、`Slow_queries` 增速、锁等待、复制延迟概览。
2. `SHOW GLOBAL STATUS LIKE 'Slow_queries'`：两次采样（间隔 ≥60s）算增速，确认是持续恶化还是单点毛刺。
3. `ssh_snapshot_get section="resource"`：DB 主机 CPU/IO 是否饱和（iowait 高说明慢查询在打磁盘）。

## 2. 定位慢 SQL（按环境能力三选一或组合）
- **有慢日志**：`SHOW VARIABLES LIKE 'slow_query_log%'` 确认开启与路径；`log_query(action="find")`/`log_query(action="tail")` 或 `ssh_execute_command "ls -l <slow_log>"` 看增长；mysqldumpslow 汇总：`ssh_execute_command "mysqldumpslow -s t -t 10 <slow_log>"`。
- **performance_schema 可用**：查 `events_statements_summary_by_digest` 按 `SUM_TIMER_WAIT` 取 Top：
  `SELECT DIGEST_TEXT, COUNT_STAR, AVG_TIMER_WAIT/1e12 AS avg_s, SUM_ROWS_EXAMINED FROM performance_schema.events_statements_summary_by_digest ORDER BY SUM_TIMER_WAIT DESC LIMIT 10;`
- **临时判定**：`SHOW FULL PROCESSLIST` 连续采样 2-3 次，找长期 `Query` 且 `Time` 大的语句。

## 3. 单条 SQL 归因（mysql_query action=explain）
对候选 SQL 逐条 `mysql_query(action="explain", datasourceId=..., sql=...)`，按优先级看：
1. `type=ALL`（全表扫描）或 `rows` 极大 → 缺索引/索引失效。
2. `Extra` 出现 `Using filesort`/`Using temporary` → 排序/去索引化，考虑覆盖索引或改写。
3. `possible_keys` 有但 `key=NULL` → 索引失效（隐式转换、函数包列、`OR`、前导模糊 `LIKE '%x'`、数据倾斜被优化器放弃）。
4. 聚合/JOIN 顺序：驱动表 rows 过大 → 调整 JOIN 顺序或分页拆分。

## 4. 常见根因 → 处置建议（先给建议，经用户确认再改）
| 根因 | 建议 |
|---|---|
| 缺索引 | 加二级索引（大表用 online DDL）；加后重新 `mysql_query(action="explain")` 验证 |
| 索引失效 | 改写 SQL 去掉对列的函数/隐式转换；`LIKE` 前缀匹配 |
| 大分页 | 改延迟关联/游标分页（`WHERE id > ?`） |
| 统计信息陈旧 | `ANALYZE TABLE <t>`（写操作，需确认） |
| 热点行锁 | 拆分更新、异步化；查 `innodb_trx` 是否有长事务 |
| 硬件/IO 饱和 | 升配/迁移/读写分离，属容量决策，交用户 |

## 5. 验证闭环
1. 变更（加索引/改 SQL）后：同一条 SQL 重跑 `mysql_query(action="explain")`，确认 `key` 命中、`rows` 下降。
2. 观察 `Slow_queries` 增速与应用侧延迟是否回落（间隔采样对比）。
3. 总结写入记忆：现象 → 根因 → 处置 → 验证结果。

## 注意
- 生产加索引前估算表大小与锁影响；大表 DDL 走 gh-ost/pt-osc 属发布流程，不在此越权执行。
- 慢查询常伴随连接数暴涨：处置时同步看 `SHOW PROCESSLIST` 聚合（user/host/state），防应用连接池打满。
- 任何 `mysql_execute` 写操作都会弹审批；给出明确 SQL 与影响行数预估，方便用户决策。
