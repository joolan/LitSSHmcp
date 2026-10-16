# Redis 内存持续增长排查 runbook（按需读取）

适用：「Redis 内存涨」「maxmemory 告警」「命中率骤降」。全程优先只读（`redis_read` / `redis_diagnostics`）；改配置/删 key 走 `redis_execute`（审批）。

## 1. 量化现状
1. `redis_diagnostics(datasourceId)` 拿内存/命中率/连接/慢日志概览。
2. `redis_read "INFO memory"` 关注：
   - `used_memory` vs `maxmemory`（占比 >80% 进入告警区）。
   - `used_memory_rss` 远大于 `used_memory` → 碎片（看 `mem_fragmentation_ratio`）。
   - `evicted_keys` 增长 → 已在逐出，策略与业务写入形态是否匹配。
   - `maxmemory_policy`：`noeviction` 时写满即报错，最危险。
3. 间隔 60s 采样两次 `used_memory`，算增长速率，区分「持续涨」与「一次性尖峰」。

## 2. 找内存去向（按风险从低到高）
1. **按类型聚合**：`redis_read "DBSIZE"`；如版本支持 `redis_read "INFO keyspace"` 看各 db key 数。
2. **抽样大 key**（禁用 KEYS/生产慎用 --bigkeys）：`redis_read "SCAN 0 COUNT 100"` 抽样，对样本 `redis_read "MEMORY USAGE <key>"`；循环几轮 SCAN 游标提高覆盖。
3. **过期键堆积**：`redis_read "INFO stats"` 看 `expired_keys`；`redis_read "TTL <key>"` 抽查应过期的 key 是否仍存活（业务没设 TTL 或惰性过期追不上）。
4. **内存碎片**：`mem_fragmentation_ratio` >1.5 且 RSS 高 → `MEMORY DOCTOR`/`INFO memory` 的 `active_defrag*`；碎片问题 ≠ 真实数据涨，勿误删。
5. **复制缓冲/客户端缓冲**：`INFO clients`、`INFO replication` 看 `client-output-buffer-limit` 类堆积（大从库重同步常见）。

## 3. 根因 → 处置（先给建议，经用户确认）
| 根因 | 建议 |
|---|---|
| 缓存对象变大 | 业务侧拆分/压缩 value；设置合理 TTL |
| 未设 TTL | `redis_execute "EXPIRE <key> <s>"` 或改业务写入（需确认批量范围） |
| 逐出追不上 | 调 `maxmemory-policy`（`volatile-lru`/`allkeys-lru` 视是否可丢）→ `CONFIG SET`，需审批 |
| 碎片 | 开启主动碎片整理（`CONFIG SET activedefrag yes`，先评估 CPU） |
| 大 key | `UNLINK` 异步删（优于 DEL）；禁止 `FLUSHALL` |
| 实在装不下 | 扩容/加节点/本地缓存兜底，属容量决策，交用户 |

## 4. 验证闭环
1. 处置后重采 `INFO memory`：`used_memory` 增速回落 / `evicted_keys` 稳定 / 占比下降。
2. `INFO stats` 命中率（`keyspace_hits/(hits+misses)`）未恶化。
3. 总结写入记忆：涨幅、去向、处置、验证数据。

## 注意
- 危险命令（`FLUSHALL`/`KEYS`/`CONFIG REWRITE`）会被直接拒绝或强确认；扫描一律 `SCAN`。
- 生产 `MEMORY PURGE`/碎片整理/`CONFIG SET maxmemory` 均属变更，先说明影响。
- 内存暴涨先排除「业务正常缓存了新大对象」——对比应用发布/流量时间线再动手。
