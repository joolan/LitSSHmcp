---
name: java-gc-jvm
description: Java 进程 GC/JVM 诊断（内存涨、GC 频繁、CPU 飙高、线程死锁、堆溢出）。适用于「Java 服务卡顿/内存高」「Full GC 频繁」「jstack/jmap」「OOM」。
appliesTo: [java, ssh, command, log]
---

# Java GC / JVM 诊断

## 步骤
1. **定位进程**：`java_processes`（可传 `appId` 过滤）拿 `pid`；`java_info(pid)` 看 JVM/启动参数/版本。
2. **堆与 GC 概览**：`java_heap(pid)`（堆使用/分代/GC 次数与耗时）。关注：
   - 老年代持续接近上限、Full GC 频繁且回收后仍高 → 疑似内存泄漏。
   - GC 停顿长（STW）→ 影响响应。
3. **线程**：`java_threads(pid)` 找 RUNNABLE 高 CPU 线程（记 `nid` 的十六进制），或 BLOCKED/死锁。
4. **高 CPU 线程深挖**：`ssh_execute_command "top -H -p <pid>"` 找高占用 tid，再用 `java_threads` 对照十六进制 nid。
5. **堆快照/直方图**（按需，注意开销）：`jmap -histo:live <pid> | head`（对象分布）；确认 OOM 时看 `dmesg -T | grep -i oom` 与容器内存限制。
6. **日志**：`log_tail`/`log_grep pattern="OutOfMemory|GC overhead|Full GC"` 关联业务错误。

## 处置
- 明确泄漏 → 保留现场（线程栈 + histo/堆 dump），重启前先把证据给用户；最小变更优先。
- 参数不合理（堆太小/GC 器不合适）→ 建议调整 `-Xmx`/GC 参数后滚动重启。
- 死锁 → 用线程栈定位持有锁的两方。

## 注意
- `jstack`/`jmap` 有一定开销；生产上避免高频 dump，`jmap -dump` 大堆可能卡顿，需用户同意。
- 重启 Java 服务属变更操作（`service_restart`/容器重启），需明确要求 + 审批。
