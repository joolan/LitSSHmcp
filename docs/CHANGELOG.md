# Changelog

本项目的所有重要变更都记录在此文件。格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [未发布]

### 新增

- **服务器快照工具 `ssh_snapshot_get` / `ssh_snapshot_refresh`**（归入 `ssh` 分组，工具总数 49 → 51）：把服务器整机态势**采集并持久化到独立库** `%APPDATA%\LitSSH\snapshots.db`（快照记录 + 快照事件流水两张表，与审计库分离，便于独立备份/清理），随时按 `serverId` 查询。默认采集四个维度：
  - `resource` 态势：负载/内存/磁盘（含使用率）/CPU 核数与型号/发行版/内核/主机名/IP/运行时长；
  - `portmap` 端口↔进程名/PID/用户↔systemd 服务**三元组 + 程序路径 `exe` + 完整启动命令行 `cmdline`**（`ss` 提取，区分 TCP/UDP、双栈 `0.0.0.0`/`::`/`*` 与 Unix socket；pid 经 `ps -o user/comm` + `readlink /proc/<pid>/exe` + `/proc/<pid>/cmdline` + `/proc/<pid>/cgroup` 补齐属主、可执行文件路径、启动命令与服务归属；`cmdline` 中常见凭据参数 password/secret/token 等自动脱敏）；
  - `nginx_tls` 站点 TLS：优先用**正在运行的 nginx 的可执行路径**执行 `nginx -T`（兼容宝塔等与 PATH 不同的 nginx），保存**完整有效配置 `effectiveConfig`**（含 include 展开），解析 `server_name`/`listen`/证书路径（支持 `{` 换行写法），并汇总**域名列表 `domains`**（每个域名是否 `ssl`、监听端口、关联证书）；`openssl` 取到期日与 SAN（DNS/IP），标出 N 天内即将过期/已过期；未检测到 nginx 时该维度 `skipped`；
  - `systemd` 健康聚合：service 单元按 active 状态计数、失败单元清单与 `is-system-running` 总态；非 systemd 系统 `skipped`。
  - `docker` 容器：`docker info` 守护进程概览（版本/容器与镜像计数/存储驱动/CPU/内存）+ `docker ps -a` 容器清单（名称/镜像/状态/端口/创建时间）+ `docker stats --no-stream` 运行容器资源（CPU/内存/网络/块IO/PIDs）；未装 docker 该维度 `skipped`，装了但守护进程不可用则 `available=true/daemonRunning=false`（降级）。
  - `security` 安全巡检（只读）：SSH 有效配置（`PermitRootLogin`/`PasswordAuthentication`/`PermitEmptyPasswords`/端口等）、防火墙暴露面（`ufw`/`firewalld`/iptables 规则数）、`fail2ban` 是否安装/运行、MySQL 匿名账户与远程 root、**可远程登录的高权账户**、系统空口令账户、sudoers `NOPASSWD`，并结合监听端口识别**公网暴露的高危端口**（3306/5432/6379/2375/9200/21/23…）；统一输出 `findings[]`（severity/id/title/detail/evidence）与 `summary` 计数。**MySQL 账户核查优先用"与该服务器匹配的已配置 MySQL 数据源凭据"查询 `mysql.user`**（回环 host + 同隧道服务器，或数据源 host == 本服务器 host/IP）；账号不必是 root，但需有 `mysql.*` 的 SELECT 权限，否则 `checked=false` 并说明原因；无匹配数据源时回退免密 best-effort（`auth_socket`/`~/.my.cnf`/`debian.cnf`）。
  - **默认只读本地快照不连服务器**；`ssh_snapshot_refresh` 为**同步阻塞**采集，描述已提示"耗时较长（典型 10~30 秒，弱网更久）"；**单飞限流**——同一服务器同时只允许一个快照，重复调用立即返回 `status=snapshot_in_progress`（含进行中的 `snapshotId`），不排队。**失败也落库**（`state=failed`，保留已采集到的部分 section 与失败事件；连接失败/超时/取消都会正确收尾，不留 `running` 孤儿）。
  - **提权可配置**：`config.json` 新增 `snapshot.useSudo`（默认 `true`）——开启且服务器配置了 `SudoType` 时自动以 sudo/su 执行**内置固定只读命令**（root 视图更完整），**不逐次弹审批**；关闭或未配置时自动降级（相关字段缺失并标注 `degraded`）。采集命令仍受命令过滤器 `Blocked` 规则约束。
  - **可扩展 + 可保留**：采集维度实现 `ISnapshotCollector` 并在 `Program.cs` 注册即可扩展（为后续趋势图/定时任务预留统一历史 `data`）；`snapshot.retentionPerServer`（默认 30，`0`=不限）按服务器保留最近 N 份并级联清理事件。
  - 每次刷新写一条审计记录（`category=probe`）；快照详情与事件在快照库内独立留存。测试期快照库格式版本为 **v2**：版本不一致时直接重建（不做历史迁移，旧快照自动清空）。

### 桌面 App

- **服务器列表右键新增「采集快照」与「快照历史」**：采集为同步操作（典型 10~30 秒），完成/失败后自动打开历史窗口并定位本次快照；**单飞限流**——同一服务器同时只允许一个采集（App 内全局忙标志 + 服务内存锁 + 库内 `Running` 唯一部分索引，**跨 App / MCP 进程**也生效），重复触发返回进行中提示。
- **新增「服务器快照历史」窗口**：通用查看页，顶部可**切换服务器**；左列历史快照列表（时间/状态/耗时/提权/错误），选中后右侧展示该次快照的**采集维度概览**（维度/状态/耗时/说明）、**采集事件**流水与**原始数据 JSON**。
- **安全设置新增「服务器快照」区**：采集是否提权（`snapshot.useSudo`）、每服务器保留份数（`snapshot.retentionPerServer`，0=不限）、采集超时秒数（`snapshot.timeoutSeconds`），读写 `config.json` 的顶层 `snapshot` 段。

### 修复

- **`mysql_diagnostics` 报"格式错"**：`SHOW FULL PROCESSLIST` 解析用了错误列序（把 `Host` 当 `db`、把 `Command`（`Query`/`Daemon`/`Sleep`…）当 `Time`），`Convert.ToInt64("Daemon")` 抛 `FormatException` 导致整个诊断失败、只能绕道 `mysql_query`。现按正确列序（`Id,User,Host,db,Command,Time,State,Info`）取值，并以不抛异常的数值转换兜底；`byDatabase` 与"最长运行查询"统计同步修正。

## [1.1.1] - 2026-10-04

### 修复

- **拓扑自动发现：本机端点需有监听证据**：配置扫描发现的 MySQL/PostgreSQL/Redis/RabbitMQ/Kafka 端点，若主机为 localhost/本机地址/指向本服务器，则必须确有对应端口在监听才生成「待确认」节点，否则跳过并记入 notes —— 修复“服务器上没有 5672/6379 监听却冒出 amqps/redis 待确认节点”的误报。远程端点行为不变；占位符/变量主机名（如 `${RABBIT_HOST}`、`<host>`）一律跳过。
- **`amqps://` 默认端口纠正为 5671**（此前误按 5672；`amqp://` 仍为 5672）。
- **MQ 端点判定收紧**：配置发现的 RabbitMQ/Kafka 端点，主机为占位符/协议名（`amqp`、`amqps`、`host`、`${...}`、`<...>` 等）一律跳过；远程主机若**不对应任何已配置服务器**，仅记入「未匹配端点」并写入 notes，不再生成 `mq:disc:` 待确认节点（修复“服务器上并无 5672 监听却冒出 amqps/amqp 待确认节点”）。
- **配置端点准入规则统一（DB + MQ）**：扫描到的数据库/缓存/MQ 端点，只有 ① 匹配到已配置数据源/服务器，或 ② 主机为本机且端口确有监听 时才生成「待确认」节点；其余（远程且未登记、占位符/协议名主机、本机未监听）只登记为「未匹配端点」，不再产生 `ds:disc:`/`mq:disc:` 噪声节点。
- **资产拓扑新增「发现报告」入口**：右键「更多操作 → 发现报告」可查看最近一次自动发现的扫描服务器、说明(notes)、未匹配端点（含来源文件、URL、跳过原因）与错误；发现完成后状态栏会提示说明/未匹配条目数。
- **修复本机/隧道数据源被交叉归属**：进程扫描与 ESTAB 连接扫描此前用 `IsLocalHost(ds.Host)` 匹配数据源，会把某台服务器上的本地 `mysqld`/`redis`（或本地 3306 连接）错配到**另一台**服务器上 `Host=localhost` 的隧道数据源（表现为“A 服务器 → B 服务器 mysql”的假关系）。现改为“同机”判定：① 数据源主机名/IP **双向**匹配本服务器；② 或数据源**跳板服务器就是本服务器**且其地址是本机地址（`127.0.0.1`/`localhost`/`::1`）或**本服务器局域网 IP**（`hostname -I`/`ip -4 addr` 采集）。否则不归属。
- **连线证据可见**：在资产拓扑中选中一条「自动发现」的连线，右侧面板会显示其**证据**（来自配置扫描的文件/URL、ESTAB 连接对端、或 mysql processlist 客户端），方便核对关系成因。
- **重新发现清空旧结果**：此前发现前只清理含 `:disc:` 的“待确认”边，导致指向**已登记资产**的旧假边（如 `ssh:A → ds:某数据源`）永远残留、重跑也不消失。现每次发现前**清空全部自动发现边与节点信息**再重建（拓扑库只存自动发现结果，人工关系在 `config.Relations` 不受影响）。
- **`ssh_execute_sudo` 结果新增 `escalation`**：标明本次实际提权机制（`direct` / `sudo` / `su` / `auto:sudo` / `auto:su` / `auto:failed`），便于排障与向用户说明“到底用了 sudo 还是 su”；工具描述改为“提权(sudo/su 由配置决定)”，客户端无需预判机制。
- **全路径密码脱敏**：SSH 密码、密钥口令、提权密码在返回给 AI 的 `output` / `error` 中一律替换为 `******`；覆盖 sudo/su/pty 与**异常**路径（Core 结果统一脱敏 + 工具层兜底），确保任何场景都不外泄密码。
- **审批模式（`security.approval.mode`）**：新增三态——`manual`（默认，所有触发审批的操作都需人工处理）、`auto-approve`（危险：所有触发审批的操作自动放行）、`auto-reject`（触发审批时直接拒绝）；桌面 App「安全设置 → 审批模式」可切换。仅影响“需人工确认”的敏感操作，命令过滤器硬拒绝（`blocked`）不受影响；自动拒绝返回 `status=rejected`（`ApprovalOutcome.AutoRejected`）。
- **审计 = 操作日志 + 审计日志（统一一张表）**：所有操作都会记录——命令/SQL 执行、审批与拦截、只读探测（连接测试、列目录）、列表元数据（列服务器/数据源/拓扑）、文件传输；新增 `Category`（`exec`/`gate`/`probe`/`meta`/`transfer`）与 `Decision`（`manual-approved`/`manual-rejected`/`auto-approve`/`auto-reject`/`timeout`/`unavailable`/`blocked`）字段并**纳入哈希链**（格式版本 v4，测试期直接重置旧库）；`ssh_get_command_history` 与桌面 App 审计窗口支持按 `category` 过滤；桌面 App 的打印式手工会话也会写入审计（`tool=desktop`）。SQL/Redis 审计同样新增 `Category`/`Decision`（`datasource_get_sql_history` 支持 `category` 过滤；格式版本 v5）。
- **`ssh_execute_command` 防挂起 + 可调超时**：新增 `timeoutSeconds`（1-3600，默认 60）；对会持续输出/需交互的命令（`tail -f`、`docker logs -f`、`journalctl -f`、`kubectl logs -f`，`vi/less/top/watch`，普通通道的 `sudo/su`，`ping` 无 `-c`，`nc/telnet`，`docker exec -it`，`docker attach`）**前置拦截**返回 `status=blocking_command` 并给出替代写法（`tail -n`/`--tail`/`--no-pager`/用 `ssh_execute_sudo`），避免无 TTY 挂起、拿不到结果；技能补充「避免挂起/丢结果」避坑章节。

## [1.1.0] - 2026-10-03

自 1.0.0 以来的变更，含本轮「SSH 服务器禁用」「拓扑新增节点避让/打开即适应」「提权 Auto 与 pty 修复」及此前累积的工具组/审计/拓扑发现增强。

### 变更（工具命中率与调用成功率专项）

#### 安全（S1）

- **`topology_discover` 命令注入修复**：扫描路径不再硬编码回退到 `/opt /home /srv /app /data`，改为只读 `security.discovery.allowedSearchPaths`；拼进远端 shell 前统一用新增的 `ShellQuote.Single/Join` 单引号转义；远端探测命令统一过 `ICommandFilterService` 并写审计。
- **SQL 只读通道补漏**：`SqlFilterService.CheckReadOnly` 增加敏感规则复检——数据修改型 CTE（`WITH ... INSERT/UPDATE/DELETE`）与 `EXPLAIN ANALYZE <DML>`（MySQL/PG 都会真的执行）不再被当作只读放行，返回 `not_readonly_statement` 并引导改用 `*_execute`。
- **命令过滤器改为 fail-closed + 整词匹配**：内置屏蔽/敏感规则下沉到 `CommandFilterConfig` 类默认值（配置缺少 `commandFilter` 段时仍有防护，不再全部放行）；`BlockedCommands`/`SensitiveCommands` 由裸 `Contains` 改为 token 边界正则匹配，消除 `chmod` 命中 `xchmodz`、`rm` 命中 `format` 之类误报。

#### 成功率（S2）

- **审批链路重构**：`IApprovalChannel.RequestAsync` 返回 `ApprovalOutcome?`（null=弃权），新增 `ApprovalOutcome { Approved, Rejected, Timeout, Unavailable }`；工具侧统一 `status`：`rejected`/`approval_timeout`/`approval_unavailable`，文案由 `ApprovalOutcomeText.Describe` 生成。默认通道改为 `["desktop","cli"]`、超时 120s→45s（无桌面环境不再必然失败）。CLI 通道按 `CancellationToken` 取消；桌面子进程排空 stdout/stderr 防污染 MCP 协议流。
- **连接失败分类**：新增 `ISshService.ProbeConnectionAsync` → `ConnectionProbeResult`（`auth`/`host_key`/`timeout`/`network`/`unknown`），`ssh_test_connection`/`ssh_get_server_status`/`mcp_self_check` 返回对应 `status`（`auth_failed`/`host_key_mismatch`/`timeout`/`connection_error`）与处置建议。
- **命令退出码与输出**：sudo 提权路径补 `; echo LITSSH_EXIT:$?` 标记并按真实退出码判定成功（修复此前恒为 `-1` 导致的假成功）；命令/SQL/历史输出按上限截断（`ToolSupport.MaxOutputChars=20000`、`MaxAuditResultChars=4000`）并给出 `truncated`/`outputChars`/`hasMore` 信号。
- **列目录不再假成功**：`ListRemoteFilesAsync` 返回 `RemoteFileListResult`，区分"目录为空"与"列目录失败"（凭据错/权限/断网不再被读成空目录）；条目超过 500 截断。
- **ID 宽松解析**：新增 `ToolSupport.FindServer/FindDatasource`（ID/名称/主机名忽略大小写），`*_not_found` 错误回显可用 ID，消除"只认精确 ID"这一最大失败来源。
- **历史分页**：`ssh_get_command_history`/`datasource_get_sql_history` 增加 `offset`，`limit` 钳制到 1–200（此前 `limit=-1` 会拉全表）。
- **审计兜底**：命令/文件传输的审计写入改为尽力而为（`ToolSupport.SafeLog*Async`），审计失败不再让"已执行"的操作报错导致模型重试重复执行。

### 修复

- `docker` 等内置安全规则在缺失配置段时不再静默失效。
- `mysql_execute`/`postgres_execute`/`redis_execute` 的审批取消信号贯通到审批与数据库执行超时。
- **配置保存改为原子写**：`ConfigService.SaveConfigAsync` 先写同目录临时文件再 `File.Move(overwrite)`，避免 MCP 按 mtime 热加载时读到"写了一半"的 JSON 而丢弃本次修改。
- **热加载解析失败不再缓存 mtime**：`SecurityOptionsProvider.ReloadUnlocked` 解析失败时保留上一次有效值且不推进 `_lastWriteUtc`，下次访问重试，避免"在界面改了但 MCP 没生效"。
- 安全设置窗口：未改动审批通道下拉时保留 config 中的原值（不再把自定义/未知通道静默覆盖为 `desktop`）；审批超时兜底默认值 120 → 45，与服务器默认一致。
- **`ssh_execute_sudo` 提权执行修复**：
  - **改用 stdin 注入密码（根治）**：审计显示 `SudoType=CurrentUser` 走交互式 shell(pty) 时命令根本没执行、`exit=-1`。现 `sudo` 改为经 exec 通道执行 `sudo -S -p '' /bin/sh -c '<cmd>'`（`/bin/sh` 兼容各发行版，不依赖 bash），并在**执行期间**把密码写入 stdin（SSH.NET 2026 要求 `BeginExecute()` 之后再取输入流）。已对真实服务器验证 `id` 返回 `uid=0(root)`。
  - 密码提示识别补全（`su` 路径保留 pty）：同时识别 `[sudo]` / `password for` / `password:` / `密码`，此前 `su - <user> -c` 的 `Password:` 不被识别导致密码从未发送。
  - 整体等待上限 30s → 120s；已喂密码后的空闲断点 3s → 15s。
  - 未回传 `LITSSH_EXIT` 标记时明确返回 `status=timeout`（不再把旧实现里"退出码 -1 也算成功"的假成功算作成功）。
  - **处理 AI 自带 `sudo` 前缀**：`sudo cmd` 会被剥离，避免在"已是 root/目标用户"的直连路径下执行无 tty 的 `sudo` 而报 `a password is required`（`sudo -u/-S` 等带选项的保持不变）。
  - **密码脱敏**：返回给 AI 的 `output`/`error` 一律把提权密码替换为 `******`，确保密码对智能体不可见。
  - 失败时给出明确 `error`（识别到 `a password is required`/`a terminal is required` 时提示检查 `SudoType`/`SudoPassword`）。
  - **新增提权方式「自动(Auto)」**：先试「当前用户 sudo」，失败（未授权/密码不通过）再回退 `su - root`，两者用同一提权密码；适配"登录账号不在 sudoers、但可以 su 到 root"或反之，无需事先判断。UI/`ssh_get_sudo_status`/指南同步。
  - **修复 pty 回显导致的退出码误判**：交互式 shell 会回显命令行（含字面量 `LITSSH_EXIT:$?`），旧逻辑一看到 `LITSSH_EXIT:` 即判结束；现只在"行首 + 标记后为数字"时才认（`StripExitMarker` 同理），修复 `su` 路径在 CentOS/RHEL 等上的假超时；空闲判定改为"仅在有新输出时刷新"、空闲 30s 退出；`su` 提示识别失败时兜底下发一次密码；无配置密码 5s 快速失败并提示。
- **多服务器防呆（指错机器）**：
  - `ToolSupport.ResolveServer` / `ResolveDatasource`：名称/主机名匹配到多个目标时返回 `server_ambiguous` / `datasource_ambiguous` 并拒绝执行（精确 ID 优先），不再 `FirstOrDefault` 静默取第一个；命令/文件/数据源等执行型工具及 `mcp_self_check` 全部改用。
  - 结果回声目标：`ssh_execute_command`/sudo/文件传输/列目录、以及新增的 `docker_*`/`service_*`/`log_*`/`java_*` 结果统一带上 `serverId`/`serverName`/`host`，便于确认没有操作错机器。
  - 审批显示主机：审批上下文传入 `名称(用户@主机:端口)`（数据源为 `类型 名称(主机:端口)`），桌面弹窗与 CLI 待决文件均可核对真实目标。
- **资产拓扑：新增节点自动避让**：手动布局(`topology-layout.json`)下，**没有保存位置**的节点（典型：新增的 SSH 服务器）会沿用默认堆叠坐标、压到用户拖过的节点上；现首次加载时为这类节点在空白处重算位置（服务器连同其托管子节点一起移动）并持久化，不再与已有节点重叠。
- **资产拓扑：打开即"适应"**：此前窗口 `Loaded` 时就 `FitView`，早于异步数据加载完成 → 实际没适应。现由 `TopologyViewModel.GraphLoaded` 事件在**图谱加载完成后**触发一次「适应窗口」（仅首次；刷新/拖动不打扰用户缩放）。

### 新增（本轮）

- **SSH 服务器「禁用」开关**：`SshServerConfig.Disabled`。禁用后 ① 不出现在 MCP 的 `ssh_list_servers`；② 所有按服务器标识解析的工具（`ssh_*` / `docker_*` / `service_*` / `log_*` / `java_*` / 应用体检等）统一返回 `server_disabled` 并拒绝执行（`ToolSupport.ResolveServer` 闸门，含 `app_health_snapshot`、`GuardedCommandService`）；③ 数据源走被禁用服务器作跳板时拒绝建立 SSH 隧道（MySQL/PostgreSQL/Redis provider）；④ 资产拓扑中不可拖线建链/改关系，服务器区块灰化并标注「已禁用」，主列表连接按钮拦截；⑤ 拓扑自动发现跳过该服务器。桌面 App 服务器编辑窗口新增「禁用此服务器」勾选框。默认关闭，兼容旧 `config.json`。
- `src/LitSSHmcp.Core/Services/Security/ShellQuote.cs`（远端命令单引号转义）。
- `src/LitSSHmcp.McpServer/Services/ToolSupport.cs`（公共支撑：截断/钳制/ID 解析/失败分类/审计兜底）。

### 新增

#### 运维领域工具组（Docker / systemd / 日志 / JVM / 应用体检，工具总数 29 → 48）

面向中小公司 SSH + Java + Docker + RDS 运维，新增 5 个工具分组、18 个工具：

- **Docker 组（`docker`）**：`docker_ps`（结构化容器列表）、`docker_logs`、`docker_inspect`、`docker_stats`、`docker_images`、`docker_restart`、`docker_exec`。容器名做字符校验 + 单引号转义；重启/exec 属敏感操作需审批。
- **systemd 组（`service`）**：`service_status`、`service_list`、`service_restart`、`service_logs`（`systemctl` / `journalctl` 封装）。
- **日志组（`log`）**：`log_tail`、`log_grep`、`log_find`，路径受新增的 `security.logs.allowedPaths` 白名单约束（防止读取 `/etc/shadow` 等），行数受 `security.logs.maxLines` 限制。
  - `ApplicationConfig.LogPaths`：应用管理新增「日志路径」，`log_tail`/`log_grep` 支持用 `appId` 代替 `path`（多个可用路径时返回 `log_path_ambiguous`，不静默猜）。
  - `log_find`：在白名单范围内按修改时间倒序发现最近写入的 `.log` 文件，用于"不知道日志路径"时先发现再读取。
- **JVM 组（`java`）**：`java_processes`、`java_threads`、`java_heap`、`java_info`（`ps`/`jstack`/`jcmd`/`jstat` 封装，pid 校验）。`java_threads`/`java_heap`/`java_info` 支持传 `appId` 代替 `pid`，按应用名/容器名在目标机 Java 进程中解析（唯一命中；0 个 `pid_not_found`、多个 `pid_ambiguous`）。
- **应用体检组（`app`）**：`app_health_snapshot`——按 `runsOn`/`connectsTo` 关系聚合某应用所在服务器的 Java 进程/容器/监听端口与依赖数据源连通性，一键拿到跨机画像；服务器探测**按应用过滤**（java 按应用名、docker 按 `containerName`、端口按应用 `port`），`notes` 说明过滤范围。
- 新增共享服务 `IGuardedCommandService`：领域工具复用统一的「ID 宽松解析 → 命令过滤 → 敏感审批 → 审计兜底 → 输出截断」链路，避免各自拼装时漏步骤。
- 配置：`SecurityConfig.Logs`（`allowedPaths` / `maxLines`）+ `ConfigMigrator` 回填 + `config.example.json` + 安全设置窗口新增「日志读取允许路径」。
- `ToolGroups.All` 扩展为 14 组；`Program.cs` 注册 16 个工具类；`docs/TOOLS.md`/README/ARCHITECTURE/`UsageGuideTools`/`McpServerInstructions` 同步，一致性测试 `ExpectedToolCount` 29 → 47。

#### 拓扑自动发现增强（更"有料"）

- **未匹配资产也成节点**：发现到的**未登记应用/容器**（`app:disc:*`）、**未配置的数据库/Redis 端点**（`ds:disc:<host>-<port>`）、**未登记的 MySQL 客户端**（`ssh:disc:<host>`）现在也会以"待确认"节点 + 关系写入拓扑缓存（`TopologyEdges`），而不再仅在返回结果里报告（以前只有"已配置资产之间"才建边）。
- **人工关系去重**：发现前收集 `relations` 的键，人工已声明的关系不再重复写入、也不再计入 `newEdges`（修此前把人工边算作"new"的偏差）。
- **PostgreSQL 端点识别**：配置文件扫描新增 `jdbc:postgresql://`（默认端口 5432），与 mysql/redis 一样建立 `canAccess` / `connectsTo`。
- **Nginx 与 MQ 端点**：扫描新增 `*.conf`（nginx）；`upstream <名>` 视为该服务器上的"待确认"应用（`app:disc:<名> runsOn ssh`），`proxy_pass http(s)://<目标>` 建立 `<应用> connectsTo <目标应用/待确认>`；RabbitMQ `amqp(s)://host:port` 与 Kafka `bootstrap.servers`/`bootstrap-servers` 建立 `<应用或服务器> connectsTo mq:disc:<host>-<port>`（新增 `mq:` 节点类型）。默认扫描路径新增 `/etc/nginx`。
- 拓扑图里 `*:disc:*` 节点标签显示为 `<名称> (待确认)`；发现结果 `notes` 说明待确认节点的含义。
- **服务进程发现**：新增按进程名扫描（`ps -eo pid,user,comm,args`），识别 `nginx/httpd/apache2/mysqld/mariadb/redis-server/postgres/haproxy/php-fpm/gunicorn/uwsgi/rabbitmq` 等并建立关系；**不依赖 systemd**（宝塔面板等直接拉起的 nginx/redis 也能发现）。
  - **精准匹配 + 归一化去重**：按 `comm`（进程名）精确匹配（不再用 `grep` 按整行匹配，避免 `user=postgres` 的桌面进程、args 含 `nodev/nnginx.conf` 的进程被误报）；`mysqld/mysqld_safe→mysql`、`redis-server→redis` 等归一化，每类服务只建一个节点。
  - **数据/应用分类**：`mysql/redis/postgres/mongod/memcached` 建**数据源**节点（已配置同类型数据源则直接连真实节点），其余建**应用**节点。
  - **端口信息**：解析 `ss -ltnp`（端口→pid/进程名）获取监听端口，参数兜底（`redis ... :6379`/`mysqld --port=3306`）；**非 root 拿不到进程名时**用"该服务常见端口 ∩ 实际监听端口"兜底（如 nginx→80/443）；端口写入节点信息并显示在待确认节点标签上（`name :80,443 (待确认)`），服务器节点也带上全部监听端口。
  - **确认弹窗预填**：一键确认时会读取节点信息，预填**类型**（如 nginx/redis）、**端口**与**应用路径**（进程可执行文件/配置文件路径），不再是默认的 java/无端口。
- **应用路径**：`ApplicationConfig.Path`（应用管理新增可选字段「应用路径」，部署目录/jar/可执行文件路径）；自动发现的应用会把推断出的路径存入节点信息，确认登记时预填。
  - **可选提权探测**：新增 `security.discovery.useSudo`（安全设置「自动发现使用提权」）。开启后监听端口/配置扫描走 `sudo`（仅当该服务器配置了 `SudoType`），`ss -ltnp` 能看到 root 服务（如宝塔 nginx）的进程名→**精确端口**；提权密码由服务端注入、不暴露给 AI；未配置 `SudoType` 时自动回退为普通执行。
- **localhost 归属**：MySQL processlist 里的本机客户端（`127.0.0.1`/`localhost`/`::1`）归属到数据库所在服务器，不再生成单独的 `localhost` 服务器节点。
- **App 确认/删除待确认节点**：资产拓扑页在**节点右键菜单**中，对待确认节点(`*:disc:*`)提供「确认节点（登记为资产）」与「删除节点（清理发现边）」——选中后弹对应「新增应用/数据源/服务器」窗口（预填名称/host/port），保存后自动把发现边重定向到新登记的资产；删除则清理该节点的发现边（MQ 端点提示手动管理）。
- **发现前先清理待确认**：每次 `topology_discover` / App「自动发现」开始前，先删除上一轮所有 `*:disc:*` 待确认节点/边，再由本次证据重建（避免旧的待确认节点残留）。
- **发现节流**：自动发现加入互斥——**同一时间只允许一个发现任务**，正在执行时新的发现直接返回 `status=discovery_in_progress`（App 里提示"已有自动发现在执行，请稍后再试"），避免并发扫描拖垮目标机与本机。
- 单测新增 `TopologyDiscoveryTests` 用例（服务进程发现、本机客户端归属）。

#### 运维排障 skill（`docs/litssh-mcp-ops-skill/SKILL.md`）

- 在 `docs/litssh-mcp-ops-skill/` 下产出面向 AI 智能体的排障 skill（工具无关，随仓库分发）：工具路由速查、标准 triage 流程、按场景处方、日志路径发现（`log_find` + 启动命令/配置文件解析），以及 **MCP 未覆盖能力经 SSH 变通**的方案（在 `docker_*`/`service_*`/`java_*` 未启用或缺失时，用 `ssh_execute_command`/`ssh_execute_sudo` 实现日志定位、端口/资源、HTTP 健康检查、OOM 排查、远端配置变更等）。
- 支持 skills 的智能体（opencode / Claude 等）会自动加载；README 增加指引。

#### 审计按会话/工具区分（MCP 会话 ID + 客户端识别 + 工具名，工具数 48 → 49）

- 每次启动 MCP 服务生成**会话 ID**（`yyyyMMdd-HHmmss-<8hex>`），`AuditLogs`/`SqlAuditLogs`（含历史归档表）新增 `SessionId` 列并自动填充（由 `AuditLogService.SessionId` 注入，工具无需改参）。
- **客户端识别**：新增 `McpSessionTracker` + 工具调用过滤器 `McpSessionFilter`，在 `initialize` 握手后从 `context.Server.ClientInfo` 取客户端名称/版本并写入新增的 `Sessions` 表（`SessionId` 主键，`ON CONFLICT` 更新最近活动；客户端信息缺失时不覆盖已有值）。
- **工具名**：同一过滤器把当前调用的 MCP 工具名放入 `AuditContext`（`AsyncLocal`），`AuditLogService` 写库时自动补到新增的 `Tool` 列（如 `ssh_execute_command`/`docker_logs`/`mysql_query`），无需各工具手动传参。
- **纳入哈希链**：命令/SQL 审计的哈希链 payload 现在包含 `SessionId` 与 `Tool`，篡改会话 ID 或工具名都会导致链校验失败（`mcp_self_check`/审计窗口的“校验完整性”）。
- **审计格式版本（`PRAGMA user_version`）**：`AuditFormatVersion = 3`；启动时若版本落后则**重建审计数据**（清空链/活动表/历史表/会话表）并写入新版本，避免旧链与新算法不一致导致校验失败（测试阶段无历史包袱）。
- 会话表 `Sessions`（`SessionId` 主键/客户端名称与版本/首末活动）；旧库平滑升级：`InitializeAsync` 用 `PRAGMA table_info` + `ALTER TABLE ADD COLUMN` 自动补列。
- 新工具 **`mcp_list_sessions`**（`guide` 组，默认暴露，可随分组关闭）：列出最近会话（会话ID/客户端/版本/首末活动），用于把审计记录映射到具体 AI 客户端。
- `mcp_self_check` 返回当前 `sessionId`/`clientName`/`clientVersion`；`ssh_get_command_history` / `datasource_get_sql_history` 新增 `sessionId` 与 `tool` 过滤参数，记录也带 `sessionId`/`tool`。
- 桌面 App「审计日志」窗口：命令/SQL 表格新增“会话”“工具”列与“会话ID”过滤框，并新增“会话”页签（会话ID/客户端/版本/首末活动），CSV 导出含会话与工具。
- 单测新增 `AuditSessionTests`（自动填充/过滤、旧库迁移、会话 upsert 与客户端信息保留、工具名自动填充与过滤）。

#### 工具分组开关（按部署裁剪，可选）

- 新增 `config.json` 的 `tools.enabledGroups`（`AppConfig.Tools` + `ToolGroups.ResolveEnabled`）：只暴露部分工具分组，降低 AI 上下文占用与误选。留空/不写 = 全部启用，`"all"` = 全部，大小写不敏感，未知分组忽略并启动告警。
- 分组与工具类映射（`Program.cs` 按分组条件注册 `WithTools<T>()`）：`ssh`、`command`、`fileTransfer`、`datasource`、`mysql`、`redis`、`topology`、`guide`。
- 若仅启用 `mysql`/`redis` 而未启用 `datasource`，启动日志会告警（缺少 `list_datasources` 获取 `datasourceId`）。
- 桌面 App 菜单 **配置 → 工具分组设置**（`ToolGroupsWindow`）可视化勾选分组（启用全部/全部停用/重新加载/保存）；`["none"]` = 全部停用。
- 单元测试新增 `ToolGroupsTests`（含配置往返），用例总数 118 → 127。

#### Redis 数据源支持（第一期：只读 + 写操作一律审批，工具总数 22 → 25）

- **自研 RESP2 协议客户端**：`RedisProtocol.cs`（`RespCodec` 编码命令 / `RespReader` 解析应答 / `RedisClient` 建连，TCP + BufferedStream），**不引入第三方 Redis SDK**；UTF-8 解码失败的 bulk 转 base64 返回。连接支持 `direct` 与 `sshTunnel`（复用 MySQL 的隧道/限流/主机密钥链路，跳板推导抽出共用 `TunnelServerResolver`），连上后按需 `AUTH`（密码/ACL 用户名只发给 Redis，不落跳板机）与 `SELECT` 默认 DB。
- **驱动接入**：`RedisDriver : IDatasourceDriver`（Test / Query / Execute / Diagnose，Explain 明确提示"Redis 没有 EXPLAIN"）；`DatasourceDriverRegistry` 登记 `mysql` + `redis`（构造函数改为同时注入 `IMySqlConnectionProvider` 与 `IRedisConnectionProvider`）。
- **命令安全策略** `RedisCommandPolicy`（第一期固定内置，暂不可配置）三档：
  - 只读白名单（`GET`/`INFO`/`SLOWLOG GET`/`CONFIG GET`/`SCAN` 等）→ `redis_read` 直接执行；
  - 危险与会让连接挂起的命令（`SHUTDOWN`/`FLUSHALL`/`FLUSHDB`/`DEBUG`/`SWAPDB`/`REPLICAOF`/`SUBSCRIBE`/`BLPOP`/`CONFIG REWRITE`/`MODULE LOAD` 等）→ **硬拒绝，不会以任何方式执行**；
  - 其余写命令（`SET`/`DEL`/`EXPIRE`/`CONFIG SET`/`CLIENT KILL` 等）→ `redis_execute` **一律弹桌面审批**（第一期不做敏感度分级），执行与否全部写审计。
- **3 个新 MCP 工具**（22 → 25）：`redis_read`（只读，`maxItems` 截断数组）、`redis_execute`（写/管理，审批）、`redis_diagnostics`（`INFO`/`DBSIZE`/`CLIENT LIST`/`SLOWLOG`/`CONFIG` 关键项 + keyspace 摘要）。命令解析 `RedisCommandParser` 支持引号与反斜杠转义，结果经 `RedisValueFormatter` JSON 化（超长字符串/超大数组截断），失败返回结构化 `status`（`blocked`/`not_readonly`/`readonly_statement`/`rejected`/`redis_error`/`connection_error`/`invalid_command` 等）。
- **配置与界面**：数据源类型项"Redis（预留）"改为正式"Redis"，**切换类型时自动带出默认端口** 3306/6379；提示文案补充 ACL 用户名与 DB 索引语义；审计窗口 SQL 列头改为"SQL/Redis命令"（Redis 命令审计写入同一 `SqlAuditLogs` 表，SQL 列记录命令原文）。
- **文档与同步**：`docs/TOOLS.md`（唯一事实来源）新增第 6 组 Redis、路由表 4 行、错误约定；README 工具表/计数/安全控制/排查链路、`ARCHITECTURE`（接口表、核心服务、时序、安全模型、扩展点）、`UsageGuideTools` 内置清单（并补上此前遗漏的 `health_check`）同步更新。
- **单元测试**：新增 `RedisProtocolTests`（RESP 编码/解析/截断/异常）、`RedisValueFormatterTests`、`RedisCommandParserTests`、`RedisCommandPolicyTests`、`RedisEndToEndTests`（内存假 RESP 服务器：验证 AUTH/SELECT 握手、只读查询、写/危险命令拦截、诊断采集），用例总数 52 → **118**。

#### MySQL 数据源与资产拓扑（第二迭代核心功能）

- **数据源模型**：`DataSourceConfig` 支持配置任意数量的 MySQL（`host/port/username/password/defaultDatabase`），两种访问模式：
  - `direct` 直连：MCP 部署机可直接访问数据库；
  - `sshTunnel` 隧道：数据库仅内网可达时，经绑定的 SSH 服务器做本地端口转发（`SshTunnel`，基于 SSH.NET `ForwardedPortLocal`），MySQL 密码只发给 MySQL、不落在跳板机；`tunnelServerId` 为空时按拓扑 `canAccess` 关系自动推导跳板。
- **驱动抽象**：`IDatasourceDriver` + `DatasourceDriverRegistry`，按 `datasource.type` 分发；提供 `MySqlDriver`（MySqlConnector）。扩展新类型（如 Redis）只需实现驱动并登记。
- **连接提供者**：`MySqlConnectionProvider` 统一建连（直连/隧道），返回 `IDatasourceSession`。
- **SQL 安全过滤**：`SqlFilterService` —— `mysql_query` 仅放行 SELECT/SHOW/EXPLAIN/DESC/WITH 单条只读语句；`mysql_execute` 中 DROP DATABASE/TABLE、无 WHERE 的 DELETE/UPDATE、TRUNCATE、GRANT 等直接拒绝，其余敏感写语句需用户桌面确认。
- **SQL 审计**：`SqlAuditLog` + `AuditLogService` 记录全部 SQL 操作（含被拒绝/被驳回），落盘 SQLite `%APPDATA%\LitSSH\audit.db`（`SqlAuditLogs` 表）。
- **拓扑模型**：节点 `ssh:` / `ds:` / `app:`，关系 `runsOn`（应用运行在服务器）、`connectsTo`（应用连数据库）、`canAccess`（服务器可访问数据库）；人工声明（`config.json` 的 `relations`）与自动发现（`TopologyEdges` 表）双轨合并。
- **拓扑发现**：`TopologyService.DiscoverAsync` 扫描 SSH 服务器上的 java 进程、ESTAB 网络连接、配置文件 JDBC 地址，并用 MySQL `SHOW PROCESSLIST` 反查客户端 IP。
- **凭据加密**：`ISecretProtector` / `DpapiSecretProtector`（Windows DPAPI，`enc:` 前缀）；`ConfigService` 读写双向迁移，磁盘无明文密码；工具入参只用 `datasourceId`，出参永不包含密码。
- **新增 10 个 MCP 工具**（工具总数 11 → 21）：
  - 数据源：`list_datasources`、`get_datasource_status`、`get_sql_history`
  - MySQL：`mysql_query`、`mysql_execute`、`mysql_explain`、`mysql_diagnostics`
  - 拓扑：`get_topology`、`get_asset_dependencies`、`discover_topology`
- **WPF 数据源管理界面**：`DatasourceManageWindow`（数据源 CRUD + 拓扑关系编辑）与 `DatasourceEditWindow`（含访问模式切换、隧道跳板下拉），主窗口新增"数据源与拓扑关系管理"入口。

#### 升级 Phase 1（安全 / 运维 / 质量）

- **测试工程**：新增 `tests/LitSSHmcp.Tests`（xUnit），覆盖 `SqlFilterService`、`CommandFilterService`、`PathPolicy`、`SecurityOptionsProvider`（热更新）、`DpapiSecretProtector`、`ConfigMigrator`、`AssetNode`，共 40 个用例。
- **MCP 工具注解**：为全部 21 个工具标注 `ReadOnly` / `Destructive` / `Idempotent` / `OpenWorld`（如 `list_*`/`get_*`/`mysql_query` 为只读，`execute_command`/`execute_with_sudo`/`upload_file`/`mysql_execute` 为破坏性），便于客户端向用户提示风险。
- **日志文件**：MCP 日志按天写入 `%APPDATA%\LitSSH\logs\mcp-YYYYMMDD.log`（保留 7 天），stderr 保留。
- **配置版本与迁移**：`AppConfig.schemaVersion` + `ConfigMigrator`；旧配置在启动时自动补写版本号。
- **公共路径与产物**：新增 `ConfigPaths` / `AppConfigJson` 统一 `%APPDATA%\LitSSH` 路径与 JSON 选项。

#### 升级 Phase 2（安全 / 可视化 / 运维）

- **SSH 主机密钥校验（TOFU）**：新增 `ISshKnownHostsStore`（`%APPDATA%\LitSSH\known_hosts.json`）；`SshClientFactory` 经 `HostKeyReceived` 校验指纹，`security.sshHostKey.mode = tofu|strict|off`（默认 tofu：首连记录、指纹变化即拒绝），SSH 与 MySQL 隧道连接均生效。
- **拓扑发现路径白名单**：`security.discovery.allowedSearchPaths`；`discover_topology` 仅允许白名单内路径，越界返回 `path_not_allowed`，留空时使用白名单默认值。
- **按目标限流**：`security.limits`（`maxConcurrentPerTarget` / `maxCallsPerMinutePerTarget`）；新增 `ITargetLimiter`，对 SSH 命令/提权/文件传输、MySQL 连接、拓扑发现按目标限流，超出返回限流错误。
- **应用(app)管理界面**：新增 `ApplicationManageWindow`，可视化维护 `applications[]`（名称/类型/端口/主机/描述），主窗口新增入口。
- **审计查看窗口**：新增 `AuditWindow`（命令 / SQL 两个页签、按服务器/数据源筛选、导出 CSV），主窗口新增入口。
- 单元测试新增 `SshKnownHostsStore`、`TargetLimiter` 用例（总数 47）。
- 说明：**C4 取消支持受 SDK 限制未实现** —— ModelContextProtocol 2.2.0 未向工具处理器暴露 `CancellationToken`，待 SDK 提供后接入。

#### 升级 Phase 3（审计治理 / 可视化 / 运维）

- **修复：拓扑关系无法编辑** —— 关系列表选中后回填编辑框，新增"更新选中"（此前只有添加/删除）；备注列改为原始值+展示值分离。
- **`health_check` 自检工具**（工具总数 21 → 22）：检查配置可读、审计库可写、已知主机库可读，可选测试指定服务器/数据源连通性。
- **审计治理（A6/C5）**：新增 `security.audit`（`storeSqlText` / `maskLiterals` / `retentionDays`）；`SqlRedactor` 对 SQL 字面量脱敏；`AuditLogService` 按保留天数自动清理超期记录。
- **安全设置页（B2）**：`SecuritySettingsWindow` 可视化编辑命令/SQL 过滤、文件传输策略、主机密钥模式、发现路径、限流与审计策略（改动经 `SecurityOptionsProvider` 热生效）。
- **拓扑可视化（B3）+ 自动发现入口（B4）**：`TopologyWindow` 按关系类型表达——`runsOn` 的应用**内嵌在所属服务器区块内**（可直接看出服务器上运行了哪些服务），`connectsTo`/`canAccess` 以带类型标注的连线绘制，人工关系实线、自动发现虚线；内置"自动发现"按钮。
- **配置导入/导出（B6）**：主窗口支持导出（可选脱敏，绝不含明文密码）与导入（导入后自动重新加密）。
- **服务器详情增强（B7）**：显示上次连接时间与该服务器可访问的数据源。
- 说明：**D5（SshService 拆分）** 属较大结构重构，风险较高，暂缓；`ConfigService` 内存缓存在热更新需求下以 `SecurityOptionsProvider` 的 mtime 缓存先行覆盖。

#### 升级 Phase 4（拓扑可视化增强 / 界面重组）

- **拓扑可视化增强**：`runsOn` 现在同时支持**应用与数据库**——两者都内嵌在所属 SSH 服务器区块内（服务器下标注 `N 应用 / M 数据库 (runsOn)`），可直观看到一台服务器上运行的多个应用与其 MySQL；节点标题附带端口（如 `订单库 :3306`、`web-01 :22`）；支持多个应用连接同一数据库；数据库运行在所连服务器上时省略冗余 `canAccess` 连线；**鼠标悬浮节点显示详情**（类型/主机/端口/账号/数据库类型/默认库/访问模式/描述/标签，密码等敏感信息不展示）；修复应用节点被误用数据库配色的问题。
- **管理功能拆分**：原"数据源与拓扑关系管理"拆为 **"数据源管理"**（`DatasourceManageWindow`）与 **"拓扑关系管理"**（`TopologyManageWindow`，关系增删改、节点/类型下拉、选中回填）。
- **主界面重排**：`MainWindow` 顶部新增分类菜单（**资产**：数据源/应用/拓扑关系/资产拓扑图；**安全**：安全设置；**审计**：审计日志；**配置**：导出/导入），主体改为三栏（服务器列表 / 服务器详情 / 命令执行与最近活动），去掉原先堆叠的按钮。
- **主界面重构（轻量 SSH 客户端风格）**：左侧服务器列表改为**两行紧凑项**（名称 + `主机:端口`，不显示描述、宽度自适应列表区）；移除常驻的服务器详情面板（信息在编辑对话框查看）；双击或「连接」在右侧打开**会话标签页**（命令输出/输入/最近活动/测试连接/关闭），空白时显示引导提示。
- **列表交互统一（右键菜单）**：服务器列表顶部只保留 添加/刷新，连接/编辑/删除 移入条目右键菜单（双击连接）；数据源管理同样保留 添加/刷新，测试连接/编辑/删除 移入右键菜单，**双击条目编辑**；应用管理改为与数据源一致（列表 + 添加/刷新 + 右键编辑/删除），并新增 `ApplicationEditWindow` 对话框编辑（原内嵌表单移除）。
- **审计日志模糊查询**：新增"命令关键字"输入框，`GetLogsAsync`/`GetSqlLogsAsync` 支持 `keyword`（`LIKE`）参数，命令审计按 `Command`、SQL 审计按 `Sql` 模糊匹配。
- **菜单新增「MCP工具说明」**（配置之后）：新窗口 `McpToolsWindow` 展示 MCP 接入说明（stdio 配置样例）、意图 → 工具路由表与全部 22 个工具的参数/返回/使用要点；左侧按章节/工具浏览、可搜索，右侧等宽字体显示，支持 **复制本节 / 复制全部说明**（可直接粘贴进 AI 智能体提示词）。
- **工具说明"双向同步"约定**：内容以 `docs/TOOLS.md` 为唯一事实来源并嵌入 App（`EmbeddedResource`）；同步约定注释写入 ① `McpServer/Tools/*.cs` 文件头（9 个工具类）② `Program.cs` 的 `WithTools<T>()` 注册处 ③ `McpToolsWindow.xaml.cs` 文件头 ④ `LitSSHmcp.App.csproj` 嵌入声明 ⑤ `UsageGuideTools.GetUsageGuide()` 内置清单，并在 `docs/TOOLS.md` 顶部写明三处必须一起改。

#### 升级 Phase 5（授权弹窗改造）

- **授权确认弹窗升级**：把原来的 Win32 `MessageBox`（P/Invoke）替换为自绘的 WinForms 对话框（`ApprovalDialog`）——**强制置顶**（可配置）、**无操作 120 秒自动拒绝**（可配置）、深色标题栏 + 命令内容滚动框 + "复制内容"按钮；环境不支持显示时按拒绝处理（fail-closed）。
- **依赖变化**：`LitSSHmcp.McpServer` 目标框架由 `net8.0` 调整为 `net8.0-windows` 并启用 WinForms；弹窗在独立 STA 线程上显示，多个授权请求串行化。
- 新增配置 `security.approval = { style, timeoutSeconds: 120, topMost: true }`。`style` 可选：`process`（**推荐**，启动独立子进程显示审批弹窗，规避宿主的隐藏窗口问题，带超时）、`dialog`（进程内自绘）、`native`（原生置顶 MessageBox，无超时）。自绘/子进程失败自动回退原生；子进程通过退出码回传结果（0=允许/1=拒绝/2=超时），请求经临时文件传递（不含密码）。失败细节写入日志。

### 安全

- **文件传输策略落地**：`FileTransferTools` 现校验 `fileTransfer.enabled` 及 `allowedLocalPaths` / `allowedRemotePaths` 白名单（此前字段已定义但未生效），越界返回 `path_not_allowed` / `file_transfer_disabled`；新增 `PathPolicy` 做路径规范化与 `..` 穿越拦截。
- **安全过滤器热更新**：`CommandFilterService` / `SqlFilterService` 改为经 `ISecurityOptionsProvider` 按配置文件 mtime 读取，修改 commandFilter/sqlFilter/fileTransfer 后**无需重启** MCP。
- **审计库加固**：`audit.db` 启用 WAL 与 `busy_timeout`，并为时间/服务器/数据源建索引。

### 修复

- **WPF "添加"按钮卡死**：`DatasourceEditViewModel` 构造函数曾在 UI 线程上同步阻塞 `LoadConfigAsync().GetAwaiter().GetResult()`，`await` 续延需回到被阻塞的 Dispatcher → 经典 sync-over-async 死锁。修复为构造函数接收 `SshServerConfig[]` 参数、`Add()/Edit()` 改为 `async` 先 await 加载再构造窗口。已用 WPF Dispatcher 线程测试桩复现（旧模式挂起）并验证修复后不卡死。
  约定已写入 [docs/ARCHITECTURE.md](ARCHITECTURE.md) 线程模型章节：ViewModel 禁用 `.Result` / `.GetResult()`。

### 变更

- **资产拓扑可视化编辑器（合并「拓扑关系管理」+「资产拓扑图」）**：两者合并为一个可交互窗口（菜单 **资产 → 资产拓扑(可视化编辑)**）。
  - **无限画布**：滚轮**缩放**、按住**中键/右键拖动平移**、工具栏 `＋/－/适应/100%`、`Ctrl+0` 重置；**关系属性面板可拖动**标题栏移动；
  - **拖动落点判定容器归属**：节点完全落入服务器 → 自动建立 `runsOn`（经 `RelationRules` 校验，含 runsOn 唯一性，不合法则禁止并回退）；从服务器内拖出 → **弹窗确认**删除 `runsOn`；服务器间直接拖动 → 确认后删除旧关系并新建；**禁止任何节点与服务器部分重叠**（必须完全在内或完全在外）。
  - **调整大小同样做重叠校验**：缩放到触碰服务器区域时即时停止（`ResizeSelected` 中 `ServerOverlapInvalid` 守卫），松开鼠标再 `ValidateResize` 复核，非法则还原；托管子节点仅在所属服务器内允许缩放。
  - **修正**：托管子节点在加载时统一 `ClampChild` 限制在所属服务器内（消除"框外却仍提示移出"的错觉）；拖出删除 `runsOn` 时同时清理自动发现缓存；仅当拖动前确实在服务器内才提示移除。
  - **服务器调整不得接触任何节点**：`ServerOverlapInvalid` 对服务器检查与所有其它节点的相交（仅允许完全包含自己的托管子节点）；普通节点仍禁止与服务器重叠。
  - **runsOn 托管节点用点线边框**渲染（应用橙/库绿，`1 3` 点线），与选中框的蓝色长虚线明显区分。
  - **画布网格背景**（20px `DrawingBrush`，随缩放平移）；**节点移动与调整大小自动吸附 10px 网格**。
  - **手动锚点不漂移**：`ComputeRoute` 对已设置端点的边按固定侧取中点，快/慢路由一致（拖节点时端点固定在该侧）。
  - **拖动性能**：拖动过程中跳过过桥（hops）计算，且网格吸附后位置未变化时跳过重算，缓解卡顿。
  - **画布网格铺满视口**：网格改用独立 `GridLayer`（`DrawingBrush.Transform` 随缩放/平移同步），修掉"缩放/平移后边缘无网格"。
  - **平移改为空白处左键拖动**（取消中键/右键平移）。
  - **悬停光标反馈**：指针在节点四边/四角显示对应缩放光标、在节点上显示移动光标。
  - **节点右键 → 查看/编辑关系**：新增 `NodeRelationsWindow`，列出该节点相关的全部关系（手动/自动发现），可编辑类型与备注（经 `RelationRules` 校验）或删除；关闭后主拓扑刷新。
  - `NodeRelationsWindow` 列表以**节点名称**显示（悬浮显示完整 ID），**当前节点红色加粗**区分；关系图节点名带端口。
  - **解除 `runsOn` 后节点归位**：`RelocateOrphanedStandalone` 将仍落在服务器区块内的独立节点移动到**就近空白处**并持久化布局。
  - **连接点可重叠**：同走廊连线不再分道错位（`LaneGap=0`），多条边在同一连接点重合，观感更整洁。
  - **连线不再标注 canAccess/connectsTo**（仅 `relatedTo` 等保留类型文字）。
  - **服务器内节点移动/缩放更顺手**：托管子节点在所属容器内允许部分重叠、松开夹回容器（`ServerOverlapInvalid` 跳过自身父容器）；命中顺序改为 **叶子节点 → 连线 → 服务器区块**（`HitLeaf/HitEdge/HitBox`），解决连线盖住节点导致拖不动。
  - **方向键移动选中节点**：`NudgeSelected`（10px 吸附 + 容器/重叠校验，可撤销）。
  - **修复服务器内节点缩放无反应**：连接端口此前与四边中点的缩放手柄**重合**且优先命中，导致抓住边中点变成"建边"而非缩放；现把端口移到节点**外侧**（不再重叠），并按 **叶子节点(2px 容差) → 连线 → 服务器区块**顺序命中。
  - **修复回退后选中框错位**：`RejectDrag` 先刷新选中框再重建连线；鼠标松开后统一 `RefreshSelection()`，落点回退时选中框回到真实位置。
  - **缩放/建边交互拆分**：缩放只在**四角**手柄；每边中点只保留**连接圆点**（建边），两者不再重叠，鼠标形状分别为对角线缩放/十字。
  - **拓扑拖动卡顿优化**：`topology-layout.json` 改为**内存缓存**（此前 `RebuildEdges` 每次鼠标移动都读盘+反序列化，是主要卡顿源）；拖动过程连线重算做**节流 + 尾部补算**（约 25ms）。
  - **可选性能日志**：设置环境变量 `LITSSH_PERF=1` 后，`CommitLayout` / `RebuildEdges(fast|full)` 耗时写入 `%APPDATA%\LitSSH\topology-perf.log`（`PerfLog`，未启用零开销）。
  - **拖动/缩放一致性修复**：缩放也走节流（`RequestFastRebuild`）并跳过吸附后未变化；`CommitLayout`/`RejectDrag` 取消残留的"尾部补算"定时器，避免松手后用快速 Z 形路由覆盖最终避障路由；完整路由在 Z 形无障碍时直接采用（`PathClear`），使拖动预览与松手后的连线路径**不再跳变**。
  - **拖动连线原地更新（性能）**：`GraphEdgeVm` 实现 `INotifyPropertyChanged`；拖动过程改为 `RebuildIncidentEdges` **只重算并原地更新与拖动节点相连的少数连线**，不再每帧 `Edges.Clear()` 全量重建，消除 WPF 容器抖动。
  - **真正无限画布**：解除节点移动的 `>=0` 限制，可越过左/上边界（网格随平移/缩放铺满视口）。
  - **拓扑路由引擎重构（性能 + 抽象）**：抽出无状态 `TopologyRouteEngine`，集中路由/避障/过桥/渲染几何，`TopologyViewModel` 委托调用：
    - **P3** 避障 A* 由 4×4 次降为**多源多目标一次**；
    - **P1** 障碍**空间索引**（均匀网格）加速线段穿障查询；
    - **P2** 过桥检测加**包围盒剪枝**；
    - **P0** 每条边/过桥各用一个 `ItemsControl` + 每项一个 `<Canvas>` 内叠加 `Path`（折线/圆点/箭头、白盘/补段/拱线），`Edges`/`Hops` 由 7 个 `ItemsControl` 降为 2 个（保持绝对坐标，端点正确落在节点上）。
  - **性能优化 P0+P1+P2（测量基线 → 消除交互浪费 → 路由共享上下文）**：
    - **P0 测量**：`PerfLog` 扩点（拖动会话 `DragSession` 汇总、`Zoom`/`Load` Scope）；引擎基准测试 `TopologyRouteEngineBenchmarkTests`（200 障碍 × 300 路由）；DEBUG 下工具栏「压测」按钮 + `LoadSynthetic` 合成图（约 208 节点/340 边，`stress:` 前缀 id、不落盘布局）。
    - **P1 消除每次鼠标移动的隐性浪费**：手柄/端口改为 **INPC 就地更新**（`UpdateSelectionRect` 每帧集合变更 10 次 → 0，不再重建容器）；`StrokeFor` 加静态 `ConcurrentDictionary` 画笔缓存（同引用使 WPF 跳过描边失效）；`GraphEdgeVm`/`SelX..SelH` 全部加**等值守卫**，`RebuildIncidentEdges` 中路径未变时跳过几何重建（`SamePoints`）；连线标签拆出独立 `Labels` 集合（无标签的边不再产生空容器）。
    - **P2 路由共享上下文**：新增 `TopologyRouteEngine.RouteContext`——障碍网格索引与排序坐标**一次构建、多条边共享**，端点盒容器排除改为查询期按候选应用（`ObstacleIndex.SegmentClear(..., fromRect, toRect)`），消除每边的过滤分配与索引重建；`BuildRoute` 提供共享上下文重载，旧签名封装为便捷入口（既有测试不受影响）。基准：**共享上下文 0.9 ms / 300 路由**（逐边旧路径 93.6 ms，逐边等价性断言保证输出一致），clear 300/300。
    - **压测图防污染**：`CommitLayout`/`RelocateOrphanedStandalone`/`EndAnchorDrag` 在合成图模式下不写 `topology-layout.json` 也不改布局缓存。
    - **新增测试** `TopologyRouteContextTests`（网格探针逐段等价、容器透明 vs 未过滤阻挡、共享上下文 = 逐边构建、端点锚定不回归），用例 166 → **170**。
  - **缩放/拖动卡顿优化（帧率 + 语义修正）**：
    - **服务器缩小不再被子节点挡住**：`ResizeSelected` 改为先按候选矩形用新纯函数 `ClampChildTo` 预计算子节点收紧位置（容器过小时按 `Clamp` 的 (min+max)/2 居中，不产生反向钳制），`ServerOverlapInvalid` 服务器分支跳过自身托管子节点——缩放全程顺滑、子节点自动跟随收紧（原语义：子节点部分越界 → 整个缩放被拒绝、原地停住，表现为"卡住/一顿一顿"）；与其它节点相交"碰到即停"的规则不变。
    - **每个鼠标移动零分配**：`ResizeSelected` 无变化早退提到 overlap 校验之前（网格吸附后多数微移动直接返回）；`ServerOverlapInvalid` 不再每次 `ToArray()`（`_allItems` 缓存于 `ApplyGraph`）；`FindItem` 三次 `FirstOrDefault` → `_itemsById` 字典；`_parentOf` 每帧反查 → `_childrenOf` 索引（`MoveNode`/`ResizeSelected`/`RebuildIncidentEdges` 的 `Where/Select/ToArray/HashSet` 全部移除）；`GraphEdgeVm.Key` 建边时算一次，拖动热路径不再做 `$"{from}|{type}|{to}"` 字符串插值。
    - **帧率探针**（`LITSSH_PERF=1` 时才启用）：拖动/缩放期间挂 `CompositionTarget.Rendering`，松手写 `FrameProbe(kind): frames= avg= max= events= tier=`（`tier=0` 为软件渲染/RDP）；与既有 `DragSession`（VM 每帧耗时）对照即可区分瓶颈在 VM 还是渲染层。
    - 单测新增 `TopologyLayoutGeometryTests`（`ClampChildTo` 内夹/不动/贴边/容器过小居中），用例 170 → **174**。
  - **修复拖动/缩放累积漂移（绝对锚定模型）**：移动/缩放曾用「上次位置 + 事件增量 + 10px 吸附」迭代推进，未跨吸附边界的增量被舍入**永久丢失**，来回移动鼠标后节点与鼠标的相对位置逐渐变化（跟不上鼠标）。现改为**绝对锚定**：
    - `BeginNodeDrag(id, anchor)` 记录鼠标锚点；`MoveNode(id, Point)` 每次从「拖动快照起点 + 相对锚点总位移」直接推导新位置（含子节点同步），`ResizeSelected(handle, Point)` 同理从 `_dragStartRect` 用新纯函数 `ComputeResize`（对边固定、最小尺寸钳制、10px 吸附）计算——结果只取决于总位移，**与事件路径无关，往返不漂移**；
    - 缩放被其它节点挡住时**以当前几何重锚**（吸收被挡位移），反向拖动无死区；方向键 `NudgeSelected` 改走增量路径 `MoveByDelta`（±10px 整数，吸附无损）；
    - 拖动状态统一 `ClearDragState()`（快照/缩放基准/锚点），`ValidateResize` 等路径补齐清理；
    - 新增单测 `TopologyLayoutGeometryTests.ComputeResize_*`（起点+总位移、振荡无漂移回归、对角锚定对边不动、最小尺寸钳制），用例 174 → **179**。
  - **选中节点四边可直接拉伸缩放**：
    - 四边内侧 6px 新增缩放带（两端各避开角手柄 8px，角手柄仍优先用于对角缩放）：悬停显示 `SizeNS/SizeWE` 光标，按住拖动即按 `ResizeSelected` 的 `n/e/s/w` 手柄调整该侧（对边固定、子节点自动收紧，复用绝对锚定模型）；中心区域仍是移动、未选中节点行为不变；
    - **建边端口移到节点外侧**（圆心距边界 `PortRadius+Gap=8px`，与边带互不重叠；`PortCenterFor` 统一计算，建边预览线也从外侧圆心出发）——原来端口圆心压在边界中点上，与拉边交互在同一位置冲突；
    - 新增单测 `EdgeHandleAt`（四边命中/角与中心排除/过小节点无边带）与 `PortCenterFor`（外侧间隙、命中盒不越界），用例 179 → **183**。
  - **工具栏精简**：移除顶部「删除选中关系」按钮（画布点选连线 + Delete 键 / 关系属性面板「删除」均可删除，`OnDeleteEdge` 仍服务于面板与快捷键）；「＋、－、适应、100%、刷新、压测」6 个按钮合并为最右侧「更多操作 ▾」下拉菜单（`ContextMenu`，100% 标注 Ctrl+0，压测项仅 DEBUG 显示），工具栏只保留「重新自动布局 / 自动发现」两个平铺按钮。
  - **导出图片（更多操作 → 导出图片…）**：把画布有效区域（`ContentBounds()` 全部内容 + 24px 边距，忽略当前缩放/平移）渲染为 PNG 保存——`RenderTargetBitmap` 临时以 `TranslateTransform` 对齐区域原点（2x 分辨率、超大图退化 1x），导出前隐藏选中框/手柄/端口/连线高亮并用 `#FAFAFA` 打底，导出后恢复选中状态与画布变换。新增 `RenderTargetExportTests`（STA 线程验证根级 RenderTransform 生效 + 世界坐标渲染，即导出平移截取的核心前提），用例 183 → **184**。
  - **新增测试工程** `tests/LitSSHmcp.App.Tests`（net8.0-windows）对 `TopologyRouteEngine` 做单元测试（Z 形、避障、快/慢一致性、过桥、简化）。
  - 节点**拖动/缩放**（拖服务器带动其内子节点；子节点限制在容器内；8 手柄缩放 + 最小尺寸）；**10px 网格吸附**；
  - 选中节点后从**边中点端口拖拽到目标节点建边**（按节点类型自动推断 `runsOn`/`connectsTo`/`canAccess`/`relatedTo`，并经 `RelationRules` 逻辑校验，**含 `runsOn` 每节点只指向一台服务器**）；
  - **点选连线**后弹出**关系属性面板**（右上角）：可改**关系类型**、编辑**备注**（`RelationConfig.Note`）并保存（经逻辑校验）；自动发现边仅可删除；
  - **点选连线删除**：手动关系删 `config.Relations`；自动发现边清理 `TopologyEdges` 缓存（新增 `ITopologyStore.RemoveEdgeAsync`）；**连线在服务器节点内也可直接点选**（选线优先于选节点，4px 容差）；
  - 选中连线后可拖动**两端锚点**指定接边位置（`AnchorSpec`，路由按固定侧走）；
  - **Ctrl+Z 撤销 / Ctrl+Y 重做**；**「重新自动布局」**清除手动布局；
  - 手动布局（节点位置/尺寸、端点锚点）持久化到 `%APPDATA%\LitSSH\topology-layout.json`（`TopologyLayout` + `ITopologyLayoutStore`），与语义配置 `config.Relations` 分离。
  - **修复**：拖动时边被简化为直线导致 `LabelPoint` 越界崩溃（折线点 <3 时改用首尾中点）；A* 索引与单条边计算加了防御，鼠标回调异常改为弹窗提示不崩溃。
- **runsOn 唯一性校验**：`RelationRules.TryValidate(from,to,type, existing, out error)` —— 一个应用/数据库只能运行在一台服务器上，已 runsOn 别的服务器时拒绝并提示（接线到编辑器与「拓扑关系管理」旧逻辑）。

- **拓扑可视化连线优化（阶段1~4）**：连线采用**避障正交路由**——在障碍(所有节点盒,Hanan 栅格)上跑 **A\***，**不直穿其它节点**；**源/目标端点从四边中点候选里择优选**（任意边/点皆可作端点），且**源/目标所在的外层服务器区块对其子节点透明**（不作为障碍，从而能进入嵌套区块到达被 `runsOn` 的数据库/应用）；找不到路径回退 Z 形。拐角**圆角化**；起点圆点、终点箭头（随类型更明显，自动发现略小且虚线）；同一走廊多条边分道错位（含对向）减少并线；交叉处**过桥**（白遮罩 + 下层补段 + 上层拱线）；层级为「服务器区块背景之上、叶子节点之下」。
- **MCP 全局开关**：`security.enabled`（安全设置窗口顶部「启用 MCP 服务」勾选框）——关闭后通过请求中间件（`McpGlobalSwitch` 注册到 `McpServerOptions.Filters.Request.CallToolFilters`）**拒绝所有工具调用**（返回 `isError=true` 与提示文本；工具仍会列出）。读取 `ISecurityOptionsProvider`（按配置文件 mtime 热更新），**无需重启** MCP。
- **拓扑关系逻辑校验**：新增 `RelationRules.TryValidate`（Core）——`runsOn` 只能 应用/数据库→服务器、`connectsTo` 只能 应用→数据库、`canAccess` 只能 服务器→数据库、`relatedTo` 通用但禁止自环、未知类型拒绝；「拓扑关系管理」添加/更新关系时校验并**弹窗**提示原因（如 `ssh:A --runsOn--> ssh:B` 会被拒绝）。
- **删除资产的关系清理与校验**：删除 SSH 服务器 / 数据源 / 应用时，先统计引用它的**手动关系记录**并在确认框列出数量与明细（存在则一并删除；不存在也会提示），同时清理引用该节点的**自动发现拓扑边**（`ITopologyStore.RemoveEdgesByNodeAsync`），避免残留孤立关系/节点。
- **Docker 应用管理（A：登记 + 安全规则）**：应用管理新增 **Docker 容器名** 字段（`ApplicationConfig.ContainerName`，类型填 `docker`），并登记 `runsOn` 关系后，AI 即可用 `ssh_execute_command`（必要时 `ssh_execute_sudo`）走 `docker ps/logs/restart/exec` 管理容器——**无需新增 MCP 工具**。默认安全规则补充：拒绝 `docker system prune`/`docker volume prune`/`docker network prune`/`docker volume rm`/`docker service rm`/`docker swarm leave`/`docker run --privileged`/`docker run -v /`；敏感（需桌面确认）`docker rm/rmi/kill/stop/restart/run/exec/cp/compose down/compose rm`。
- **Docker 拓扑发现（B）**：`topology_discover` 增加 `docker ps` 容器扫描（`DiscoveryResult.DockerContainers`），按 `ContainerName`/应用名匹配已登记应用，**自动建立 `app --runsOn--> ssh` 关系**。
- **修复：`redis_read`/`redis_execute` 的 `outputSchema` 非法导致部分 MCP 客户端拒绝加载**：动态字段 `result`（`object?`）被 SDK 生成为布尔 schema（`true`），客户端校验报 `outputSchema.properties.result: Invalid input`；改为把结果序列化为 **JSON 文本字符串**（schema 为 `["string","null"]`）。`json_query` 等其余工具的 schema 不受影响。
- **审计防篡改（HMAC 哈希链）+ 超期记录永久归档**：`AuditLogService` 新增哈希链（`AuditChain` 表；有本地签名密钥时用 HMAC-SHA256，密钥 DPAPI 保护于 `audit.db.key`，否则退化为 SHA-256）；写审计时在同一事务内追加链记录。保留策略由"删除"改为**移动到历史表** `AuditLogsHistory`/`SqlAuditLogsHistory`（永久保留；链不删除、记录 Id 不变，故整链始终可验证）。新增 `IAuditLogService.VerifyChainAsync()` 与模型 `AuditVerifyResult`；App「审计日志」新增 **校验完整性** 按钮与 **含归档** 筛选（`GetLogsAsync/GetSqlLogsAsync(includeHistory)`）。
- **审批带外通道（CLI/IPC，无 HTTP）**：`IApprovalService` 改为多通道分发 `ApprovalService` —— `security.approval.channels`（默认 `["desktop"]`，可加 `cli`）各通道并发等待，**首个给出决定者生效**，全部弃权或超时→拒绝（fail-closed）。`cli` 通道写 `%APPDATA%\LitSSH\approvals\pending-<id>.json` 并轮询决策文件；新增 CLI `litssh approvals` / `litssh approve <id>` / `litssh deny <id>`（适配无桌面/headless）。安全设置窗口新增「审批通道」配置；共享文件格式 `ApprovalFileStore`（Core）。
- **`LITSSH_DATA_DIR` 数据目录覆盖**：`ConfigPaths` 支持环境变量覆盖 `%APPDATA%\LitSSH`（测试隔离/便携部署）。
- **MCP 协议集成测试 + 数据目录隔离**：新增 `tests/LitSSHmcp.McpServer.Tests/McpProtocolIntegrationTests.cs` —— 以 stdio 真实启动服务器进程、走 `initialize → tools/list → tools/call` 全链路，校验 29 个工具、`inputSchema`/`outputSchema` 存在性、结构化错误（`mysql_query` 非法数据源）与 text(JSON) 兼容层、`mcp_self_check` 成功。
- **结构化输出（全量，除使用指南）**：**除 `mcp_usage_guide`（使用指南文档）外，全部 28 个工具**改为返回 DTO（`[McpServerTool(UseStructuredContent = true, OutputSchemaType = ...)]`，DTO 集中在 `McpServer/Services/ToolResults.cs`），由 SDK 生成 `outputSchema` 并**同时**产出 `structuredContent` 与等价 text(JSON)（向后兼容）。契约变化：`ssh_list_servers`/`datasource_list`/`ssh_get_command_history`/`datasource_get_sql_history`/`ssh_list_files` 由裸数组改为 `{ success, count, ... }`/`{ success, path, files }` 包裹；命令类返回 `output`/`error`（替换原 stdout/stderr 描述）；字段统一 camelCase（拓扑/SSH/数据源）。
- **数据源治理（每数据源，可选）**：`DataSourceConfig` 新增 `MaxRows`（查询上限）、`TimeoutSeconds`（命令超时）、`ReadOnly`（拒绝写操作）、`WriteApproval`（`Always`=一律桌面确认 / `AutoApprove`=受信任自动放行）；`mysql_*`/`postgres_*`/`redis_*` 均按此治理（只读返回 `readonly_datasource`；自动放行仅跳过桌面确认，仍受硬拦截规则约束）。桌面数据源编辑窗口新增对应控件。
- **查询结果列级脱敏**：新增 `SecurityConfig.Masking`（规则 `{ column: 列名正则, mode: full|email|phone|last4 }`）与 `ResultMasker`；`mysql_query`/`postgres_query` 返回前按列名对单元格脱敏。安全设置窗口可编辑（每行 `列名正则=模式`）。
- **PostgreSQL 数据源（工具总数 25 → 29）**：新增 `PostgresConnectionProvider` + `PostgresDriver`（Npgsql；直连/SSH 隧道；诊断采集 `pg_stat_activity`/`pg_stat_database`/`pg_stat_replication`）与工具 `postgres_query` / `postgres_execute` / `postgres_explain` / `postgres_diagnostics`（分组 `postgres`，路由键与「工具分组设置」一致）；数据源类型新增 PostgreSQL（端口默认 5432）。驱动注册续用 `IEnumerable<IDatasourceDriver>`，新增类型零改注册表。
- **MCP 协议增强（会话指令 + 进度通知）**：`initialize` 返回 **Server Instructions**（`Services/McpServerInstructions.cs`：按意图选组、ID 获取、写操作需桌面确认、不含密码；详细路由仍由 `mcp_usage_guide` 提供）；`ssh_upload_file`/`ssh_download_file` 通过注入的 `IProgress<ProgressNotificationValue>` 向前端推送 `notifications/progress`（字节数/百分比）。SDK 也支持 structured output（`[McpServerTool(UseStructuredContent=..., OutputSchemaType=...)]` + 返回强类型对象），因需把全部工具改为返回 DTO，列为后续项。
- **工具清单单一来源 + 一致性守门测试**：`get_usage_guide` 的 `tools` 清单不再手写，改为对程序集中所有 `[McpServerTool]` 方法的 `Name` + `[Description]` **反射生成**；新增测试工程 `tests/LitSSHmcp.McpServer.Tests`（net8.0-windows）断言"已注册工具 ↔ `docs/TOOLS.md` ↔ `get_usage_guide` 清单"三者集合一致、工具数 25、分组键等于 `ToolGroups.All`——工具增删改名后漏更新文档会直接测试失败。
- **驱动注册可扩展（DI 化）**：`DatasourceDriverRegistry` 构造改为注入 `IEnumerable<IDatasourceDriver>`，按驱动自报的 `Type` 建立索引；`Program.cs` 以 `AddSingleton<IDatasourceDriver, MySqlDriver/RedisDriver>()` 注册。新增数据源类型只需实现驱动并注册 DI，不再改动注册表构造函数（App 端 `AppServiceFactory`/`DatasourceEditViewModel` 同步）。
- **「MCP工具说明」窗口分组与「工具分组设置」对齐**：`docs/TOOLS.md` 结构调整为 `## 概览与接入` + 8 个分组章节，分组标题格式 **`## <中文名>（<分组键>）`**，分组键与 `config.json` 的 `tools.enabledGroups`（App「工具分组设置」）严格一致；`McpToolsWindow` 解析规则改为：`### \`工具名\`` 计为工具、其它 `###` 子标题并入所属章节内容（不再误判为工具/分组）。窗口顶部显示"共 25 个工具 · 8 个分组"，当分组键与 `ToolGroups.All` 不一致时给出告警。
- **工具命名规范与全量重命名（破坏性；测试阶段无历史包袱）**：统一为 **`<域>_<动作>[_<对象>]`** 的 snake_case（域前缀 `ssh`/`datasource`/`mysql`/`redis`/`topology`/`mcp`），并在 `docs/TOOLS.md` 新增「工具命名约定」。旧 → 新映射：
  - `list_servers`→`ssh_list_servers`、`get_server_status`→`ssh_get_server_status`、`test_connection`→`ssh_test_connection`
  - `execute_command`→`ssh_execute_command`、`execute_with_sudo`→`ssh_execute_sudo`、`get_sudo_status`→`ssh_get_sudo_status`、`get_command_history`→`ssh_get_command_history`
  - `upload_file`→`ssh_upload_file`、`download_file`→`ssh_download_file`、`list_remote_files`→`ssh_list_files`
  - `list_datasources`→`datasource_list`、`get_datasource_status`→`datasource_test_connection`、`get_sql_history`→`datasource_get_sql_history`
  - `get_topology`→`topology_get_overview`、`get_asset_dependencies`→`topology_get_dependencies`、`discover_topology`→`topology_discover`
  - `get_usage_guide`→`mcp_usage_guide`、`health_check`→`mcp_self_check`；`mysql_*` / `redis_*` 保持不变。
  工具名改用显式 `[McpServerTool(Name = "...")]`；同步更新描述互引、`UsageGuideTools` 内置清单、App 工具分组提示、README/ARCHITECTURE/`docs/TOOLS.md`。**AI 客户端系统提示词中引用旧名的需同步更新。**
- **工具描述瘦身（降低 AI 上下文与误选）**：25 个工具的 `[Description]` 由"长句式场景枚举"压缩为 1~2 句（做什么 + 关键互斥提示），描述总字符数 **2674 → 1291（−52%）**，单条最长 228 → 115；被移除的长篇"何时用 / 不要用"统一收敛到 `docs/TOOLS.md` 的「意图 → 工具路由表」与 `get_usage_guide`（新增 `file_transfer_guide`），并在工具文件头与 `docs/TOOLS.md` 顶部写明"描述保持精简"的约定。
- **MCP 工具说明意图路由优化**：`[Description]` 改写为"当用户问…时使用"句式，并在易混工具间加反向提示（`list_servers` vs `list_datasources`、`test_connection` vs `get_datasource_status` 等）；`datasourceId` 参数统一标注"可用 list_datasources 列出"；`get_usage_guide` 新增 `datasource_query_guide` 意图表。解决"用户想看 MySQL 列表/测连接时 AI 选错工具"的问题。
- **文档拆分**：README 精简为项目门面（特性/快速开始/示例/CLI），新增 `docs/ARCHITECTURE.md`（架构）、`docs/TOOLS.md`（工具参考与路由表）、`docs/CHANGELOG.md`（本文件）。

## [1.0.0] - 2026-09-19

初始发布（commit `cd3621b`，badge 更新见 `8891f60`）。

### 新增

- **MCP 服务器**：stdio 传输（ModelContextProtocol 2.2.0），11 个工具：
  - SSH：`list_servers`、`get_server_status`、`test_connection`
  - 命令：`execute_command`、`execute_with_sudo`、`get_sudo_status`、`get_command_history`
  - 文件：`upload_file`、`download_file`、`list_remote_files`
  - 指南：`get_usage_guide`
- **安全控制**：命令黑名单/敏感命令桌面确认（Win32 MessageBox）、文件传输审批、命令审计、sudo 提权（CurrentUser/RootUser/CustomUser）、凭据不入 AI 上下文。
- **WPF 管理界面**：SSH 服务器增删改（`MainWindow` + `ServerEditWindow`）。
- **CLI**：`litssh list` / `litssh connect` / `litssh run`。
- **存储**：`%APPDATA%\LitSSH\config.json` 配置 + `audit.db` SQLite 审计。

[1.1.1]: https://github.com/joolan/LitSSHmcp/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/joolan/LitSSHmcp/releases/tag/v1.1.0
[1.0.0]: https://github.com/joolan/LitSSHmcp/releases/tag/v1.0.0
