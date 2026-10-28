---
name: redis-troubleshoot
description: Redis 内存/命中率/慢日志/大 key/连接问题排查。适用于「缓存慢」「内存涨满」「命中率低」「Redis 连不上/阻塞」。
appliesTo: [datasource, redis, ssh]
triggers: [Redis 内存, Redis 连不上, Redis 连接失败, 缓存慢, 缓存异常, 命中率低, 大 key, evicted, maxmemory, Redis 阻塞, 慢日志]
symptoms: [redis_memory, redis_connection, cache_miss, eviction]
assets: [redis, ds]
---

# Redis 排障

## 获取标识
`datasource_list` 拿 `datasourceId`（type=redis）。只读用 `redis_read`（白名单命令），写用 `redis_execute`（需审批），体检用 `redis_diagnostics`。

## 步骤
1. **体检**：`redis_diagnostics(datasourceId)` 看内存使用、命中率、连接数、慢日志概览。
2. **内存**：`redis_read "INFO memory"` 看 `used_memory`/`maxmemory`/`evicted_keys`；`redis_read "MEMORY DOCTOR"`。
3. **命中率**：`redis_read "INFO stats"` 看 `keyspace_hits`/`keyspace_misses`。
4. **大 key / 热点**：`redis_read "SCAN 0 COUNT 100"` 抽样，结合 `redis_read "MEMORY USAGE <key>"`；生产慎用 `--bigkeys`（阻塞）。
5. **慢命令**：`redis_read "SLOWLOG GET 20"`。
6. **阻塞/连接**：`redis_read "INFO clients"`；确认是否有阻塞命令或连接泄漏。

## 处置
- 内存逼近上限且 `evicted_keys` 增长 → 调整 `maxmemory-policy`（如需 `redis_execute CONFIG SET`，需审批）或清理无用大 key（`redis_execute DEL/UNLINK`，需确认）。
- 大 key → 拆分；热点 key → 本地缓存/分片。
- 变更后复看 `INFO memory` 与命中率。

## 注意
- 危险命令（`FLUSHALL`/`KEYS`/`CONFIG REWRITE`/阻塞类）会被直接拒绝或强确认；优先 `SCAN` 而非 `KEYS`。
- 写/管理命令一律弹桌面确认。

## 深入（按需 skill(action="read")）
内存持续增长 runbook：`references/memory-runbook.md`（增长速率量化、大key/过期键/碎片归因、处置与验证）。
