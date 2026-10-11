---
name: cpu-memory-high
description: CPU 飙高 / 内存泄漏 / OOM / 负载高定位。适用于「CPU 100%」「内存一直涨」「被 OOM Killer 杀了」「load 高」「服务卡顿」。
appliesTo: [ssh, command, java, log]
---

# CPU / 内存 / 负载飙高排查

## 步骤
1. **总体态势**：`ssh_snapshot_get section="resource"` 看 CPU/内存/swap/负载与 `topByCpu`/`topByMemory`/`warnings`/`riskLevel`。
2. **定位进程**：`ssh_execute_command "ps -eo pid,ppid,pcpu,pmem,rss,comm --sort=-pcpu | head -15"`（内存把 `-pcpu` 换 `-rss`）。
3. **单进程深挖**：
   - Java → `java_threads(pid=)` 找高 CPU 线程（记下 nid 转十六进制），`java_heap` 看堆/GC。
   - 其他 → `top -H -p <pid>` 或 `cat /proc/<pid>/status | grep -i vm`。
4. **内存专项**：`free -h`；`dmesg -T | grep -i "killed process|oom"` 确认是否 OOM；`smem`/`ps_mem` 看真实占用。
5. **系统级**：`vmstat 1 5`、`iostat -x 1 3`（是否 IO 等待导致 load 高）、`ss -s`（连接数）。

## 处置
- 泄漏/失控进程：确认后重启该服务（`service_restart`/容器重启，需确认）；保留现场（线程栈/堆直方图）。
- Java：拿 `jstack` 线程栈 + `jmap -histo` 对象分布给用户判断。
- 短时突发：结合上游流量（nginx access 日志量）判断是否需限流。
- 变更后复测资源曲线。

## 注意
- 不要贸然 `kill -9`：先确认进程作用与依赖，优先优雅重启。
- 涉及重启/杀进程属破坏性操作，需用户明确要求与审批。
