# Changelog

本项目的所有重要变更都记录在此文件。格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### 新增

- **历史与会话列表按用户隔离（P0-2）**：Web 强制模式下 `ssh_get_command_history` / `datasource_get_sql_history` / `mcp_list_sessions` 默认只返回当前登录用户的数据；新增 `allUsers` 参数，仅管理员可显式查看全局。Web 宿主新增 `LITSSH_AUTHZ_ROLE` 角色注入，`Sessions` 表新增 `OwnerUserId`（旧库自动迁移），会话记录按宿主注入的归因用户落库。普通成员伪造 `allUsers=true` 或传入他人 `sessionId` 均无法越权；桌面未启用强制模式时保持原有全量行为。
- **全局清单与拓扑查询按授权收口（P0-B）**：Web 强制模式下 `ssh_list_servers` / `datasource_list` 只返回命中 `read` 白名单的目标；数据源列表同时隐藏未授权隧道服务器与 `accessibleFromSshServers` 关联引用。新增 `ToolSecurityDescriptor.SelfScopedGlobal` 显式标记“工具自身按用户裁剪”的全局清单；其余无目标全局查询（`topology_get_overview` / `topology_get_dependencies` / `topology_search` / `topology_neighbors` / `topology_path` / `topology_impact` / `topology_explain`）在强制模式下仅管理员可用，避免普通成员读取无法按目标裁剪的全量资产图。管理员与桌面未强制模式保持原行为。
- **工具安全目录与 read/write/exec 授权级别**：新增 `ToolSecurityCatalog` / `ToolSecurityDescriptor`，为全部 61 个 MCP 工具统一声明目标形态、目标参数、最低访问级别与写锁需求；`McpAuthorizationFilter` 改为按目录解析并 fail-closed，覆盖 `log_*` / `docker_*` / `java_*` / `service_*` / `topology_*` / `app_health_snapshot` 等此前未校验的目标工具；`ssh_execute_command` / `ssh_execute_sudo` / `docker_exec` / `ssh_snapshot_refresh` 要求 exec，`app_health_snapshot` 扩展到应用关联的服务器/数据源，`topology_discover` 要求全部涉及服务器授权。新增 `write` 级别（read &lt; write &lt; exec），同一用户可用多行规则分别授权不同目标；SQL/Redis 执行按实际语句动态要求 read/write，SQL `Sensitive` 不再当只读。桌面授权矩阵同步 0=read / 1=write / 2=exec，工具目录一致性测试强制注册工具与安全目录同名同数。
- **SSH 会话 · 日志文件「终端操作」右键菜单**：左侧远程文件面板对文件新增「终端操作」二级菜单，把拼好的命令直接发送到右侧交互式终端（仅在终端已连接且回到 shell 提示符时发送，未连接 / 全屏程序 / 疑似忙碌会提示原因）。内置：搜索最近 1 小时日志、搜索指定时间范围、实时跟踪（tail -f）、查看最新 N 条、查看错误 / 异常（error/exception/timeout/OOM 等）、关键字上下文（前后 3 行）、统计关键字次数、实时跟踪并过滤关键字、用 less 打开并跳到末尾（`q` 退出）、筛选并保存到文件（日志切片，可按关键字 / 时间 / 扫描行数过滤后写入远端文件，完成后刷新列表即可下载）；发送成功后自动把键盘焦点移回终端；文件面板底部结果固定单行省略显示，鼠标悬浮查看完整内容，避免挤压文件列表；另有确认提示防止误占终端。时间过滤支持行首 ISO 时间戳（`yyyy-MM-dd HH:mm:ss` 或 `T` 分隔）、nginx 错误日志的 `yyyy/MM/dd` 以及 nginx/Apache 访问日志的 `[dd/Mon/yyyy:HH:mm:ss`，保留命中时间戳后的续行，搜索默认扫描最近 20 万行；syslog 格式提示改用关键字 / 最新 N 条。所有路径、关键字与时间参数均经 shell 单引号转义，命令保持单行注入。

- **MCP 工具收敛 Phase A7（审计/会话与 SSH 连通性）**：删除 `ssh_get_command_history` / `datasource_get_sql_history` / `mcp_list_sessions`，审计历史改由 App/Web 审计页面查看；合并 `ssh_get_server_status` 到 `ssh_test_connection`（底层同为 `ProbeConnectionAsync`）。默认外部工具数降至 **36**；同步安全目录、Usage Guide、README、App 文档与 Skill。
- **MCP 工具收敛 Phase A6（拓扑查询）**：新增 `topology_query`（`action=search/neighbors/path/impact/explain`），合并并删除 `topology_search` / `topology_neighbors` / `topology_path` / `topology_impact` / `topology_explain`；`topology_get_overview` / `topology_get_dependencies` / `topology_discover` / `topology_timeline` / `topology_diff` 保持不变。默认外部工具数降至 **40**；Graph-RAG 同步改用 `topology_query`，返回 `result` 为对应动作的结构化结果。
- **MCP 工具收敛 Phase A5（Docker 读族）**：新增 `docker_query`（`action=ps/logs/inspect/stats/images`），合并并删除 `docker_ps` / `docker_logs` / `docker_inspect` / `docker_stats` / `docker_images`；`docker_restart` / `docker_exec` 保持不变。默认外部工具数降至 **44**；统一返回包含 `containers` 或 `output`；同步安全目录、Usage Guide、README、App 文档与 Skill。
- **MCP 工具收敛 Phase A4（MySQL/PostgreSQL 读族）**：`mysql_query` 与 `postgres_query` 增加 `action=query/explain/diagnostics`，合并原 `*_explain` / `*_diagnostics`；删除这 4 个独立工具，`*_execute` 写工具保持不变。默认外部工具数降至 **48**；统一返回增加 `action`、`summary`、`data` 字段，安全目录、使用指南、App 文档与 Skill 同步。
- **移除旧工具名兼容别名**：删除 `LegacyFileTransferTools` / `LegacyServiceTools` / `LegacyLogTools` / `LegacyJavaTools` 与 `tools.legacyAliases` 配置，旧名不再注册、不可调用；最终注册 **53** 个工具（52 常规 + 1 宿主内部）。旧客户端需改用合并后的新工具名。
- **MCP 工具收敛 Phase A3（日志/Java 读族）**：新增只读 `log_query`（`action=tail/grep/find`）与 `java_diagnostics`（`action=threads/heap/info`），删除旧 `log_tail` / `log_grep` / `log_find` / `java_threads` / `java_heap` / `java_info`；`java_processes` 保留用于发现 pid。默认外部工具数降至 52；同步安全目录、使用指南、README、架构、Skill 与目录一致性测试。
- **MCP 工具收敛 Phase A2（systemd 读族）**：新增只读 `service_query`（`action=list/status/logs`）统一 systemd 查询，删除旧 `service_status` / `service_list` / `service_logs`；`service_restart` 保持独立写工具。同步安全目录、使用指南、README、架构与目录一致性测试。
- **本机多终端（v1）**：AI 会话新增 `local_shell_open` / `local_shell_list` / `local_shell_read` / `local_shell_stop`，可同时管理最多 4 个独立 PowerShell/ConPTY 终端，并行跑后端、前端、下载与脚本测试；每个终端有 `term-N` ID、状态与独立输出缓冲，`read` 按 `cursor` 增量返回（默认 8000、上限 200000 字符），自动清理 ANSI 与内部同步标记。`open`/每命令仍逐次人工审批，硬白名单与危险命令拦截不变；`list`/`read` 只读，`stop` 免审批但写审计，并在停止后自动关闭对应终端标签（命令已结束时也会关闭）。`local_shell` 新增可选 `terminalId`：不传时用默认共享终端，传入空闲的 `term-N` 时把一次性命令注入该终端（长驻命令未结束时拒绝），因此可在刚打开的终端里继续执行 `ping -n 3` 等命令而无需重开；默认 CLI 白名单补充 `ping`（一次性 `local_shell` 仍拦截无限 `ping -t`）。v1 不开放任意 stdin 写入；裸 shell/REPL、`Start-Process`/`Start-Job`、尾部 `&` 等后台化写法前置拒绝，`tail -f`/dev server/构建/下载等长驻任务可用。终端复用现有本机会话标签实时显示，标题带 `[AI-进程Id]`；关闭标签、删除 AI 会话或关闭会话窗口都会回收对应进程树。
- **资产图查询（P0）**：新增 5 个只读 MCP 工具 `topology_search` / `topology_neighbors` / `topology_path` / `topology_impact` / `topology_explain`——在现有拓扑图上做搜索、邻域、有向链路、反向影响面与证据解释；不新增表、不修改采集器，全部归入 `topology` 工具组。`topology_impact` 默认排除 `:disc:` 待确认节点并单独计数；`topology_explain` 返回来源（manual/discovered）/证据/末次发现/`stale`（超过 7 天未确认）。
- **快照图谱（P1）**：`topology_discover` 现在会读取最近快照的 `systemd` / `docker` / `portmap` / `nginx_tls`，生成 `service:*` / `container:*` / `deploy:*` / `domain:*` / `port:*` / `cert:*` 节点与 `runsOn` / `listensOn` / `deployedFrom` / `hostedOn` / `exposes` / `tlsBy` / `resolvesTo` 边；系统服务同时由 systemd 单元和监听端口的 `unit` 交叉生成，容器按已登记应用的 `containerName` + 镜像 tag 推导部署记录。边新增 `Confidence`/`Disputed`/`ValidFrom`/`ValidTo`/`LastVerifiedAt` 并支持旧 SQLite 库增量迁移；图查询、影响面、`kinds` 过滤、拓扑提示与 App 类型标签同步扩展。App 拓扑新增服务/容器/部署/域名等类型配色、按图层隐藏节点、证书 30 天内黄色/过期红色边框、冲突边红色 `⚠` 标记与「确认无冲突」操作，以及选中节点后的反向 2 跳影响面高亮（非影响节点/边降透明并显示影响数量）。
- **关系冲突自动检测（P1）**：读取图谱时核对 `runsOn` / `hostedOn` 发现边——同一 `(from, type)` 出现多个目标或与人工配置不一致时标记 `disputed` 并写入 `disputeReason`；App「确认无冲突」写入 `disputeAcknowledged`，冲突仍在但不再标红，冲突消失后自动清空原因与确认状态。`topology_explain` 同步返回 `disputeReason` / `disputeAcknowledged`。
- **拓扑时间线与快照 Diff（P2）**：新增只读 MCP 工具 `topology_timeline` / `topology_diff`，按快照历史重放 `systemd` / `docker` / `portmap` / `nginx_tls`，返回每个时点的节点/边及相对上一次的新增、移除；`topology_diff` 默认比较最近两次快照，支持指定 `fromSnapshotId` / `toSnapshotId`。
- **Graph-RAG 发送前图谱上下文（P2）**：AI 助手每轮发送前从问题中解析已知资产（也回退到 `topology_search`），自动调用 `topology_neighbors` 拉取 1 跳邻域，作为临时系统上下文注入；App 以「资产图谱」步骤展示注入内容，Web 推送 `asset_graph_context` 事件，失败静默不影响对话。
- **记忆资产过滤**：召回前先取本轮 Graph-RAG 的种子资产，过滤结构化记忆；`raw` 与无 `assetIds` 的通用经验保留，资产 ID 按逗号边界匹配（`ssh:s1` 不会误中 `ssh:s10`），最多 20 个，降低跨服务器/应用的经验串味。
- **资产提示稳定摘要**：`AssetGraphPromptFormatter` 上限从 4000 收紧到 2000 字符，只保留人工配置的服务器/数据源/应用与关系，不再注入应用路径、日志、时间戳等发现细节；超长先丢弃关系再按行截断，并提示改用 `topology_*` 查询，稳定前缀缓存并减少误导。
- **拓扑时间线桌面视图**：资产拓扑「更多操作 → 快照时间线…」按服务器回放最近 N 份快照，展示节点/边规模与相对上一份的新增、移除明细，支持点选时间点及跳转快照历史。
- **内置技能包可升级**：稳定目录新增 `.litssh-bundle.json`（逐文件 SHA256）。升级时只覆盖“上次随包且用户未修改”的文件，保留用户编辑；老稳定目录无清单时一次性接管；用户删除的随包技能不会复活，新随包技能自动安装。
- **图查询歧义防护**：`topology_*` 的资产解析对大小写 ID、后缀、Label 多命中返回 `asset_ambiguous` 并列出候选，空 ID 返回 `asset_id_required`，避免静默命中错误资产；补充搜索/邻域/影响面截断、过期边与 1000 节点规模测试。
- **GitHub Actions CI**：新增 `.github/workflows/ci.yml`，在 Windows runner 上执行 `dotnet restore/build` + 5 个测试项目 + Web 前端 JS `node --check`。

### 重构

- **命令安全解析器统一**：新增 Core `CommandLineSyntax` 作为唯一解析实现，集中 Windows 单命令语法校验、首命令/参数拆分与 POSIX argv 拆分；`PosixCommandLine` 保留为兼容外观，`LocalCommandPolicy` 只保留白名单/危险规则/交互策略，不再各自维护扫描逻辑。补齐语法错误分类与两入口一致性测试。
- **质量门禁**：新增 `.editorconfig` 与 `Directory.Build.props`（警告即错误）；CI 增加分程序集覆盖率阈值，以及工具目录、授权、路径、命令、脱敏、Web、结构七个风险域的测试门禁（含最小用例数，防止测试被静默删除）。结构域新增 ViewModel 行数预算与 `MessageBox.Show`/`new ConfigService()`/`Process.Start` 基线，禁止拆分前继续膨胀。

### 修复

- **审批推送资源治理（P2-2 收尾）**：`ApprovalPushClient` 由无界 Channel 改为容量 128 的有界队列，慢客户端积压时丢弃最旧帧；审批与主会话 WS 单帧发送均增加 10 秒超时，审批连接与取消联动，主会话泵发送失败即停止，半开/慢连接不再无限占用推送泵。前端既有的 5 秒 REST 对账与重连快照负责恢复完整状态。补有界队列丢弃旧帧回归测试。
- **子代理继承策略与预算（P2-3）**：`SubAgents.Create` 显式接收并应用父会话 `ToolPolicy` 与 `PlanMode`，并在每次调用时通过 provider 实时读取，父会话运行期切换策略/计划模式后子代理立即生效；父会话 `Deny` 的工具不会因子代理持有 `_subTools` 而复活，计划模式下子代理也不能执行写工具；子代理 token 在结束/失败/取消时回传父会话。新增父子/并行子代理共享的 `SessionCostBudget` 账本，每次模型响应原子累加增量 token，预算检查读取全局总量，避免各算各的并发超额；补 Deny/计划模式、运行期切换、token/usage 增量、预算耗尽与并发账本测试。
- **图片附件边界校验（P2-8）**：上传不再信任扩展名/MIME，按 PNG/JPEG/GIF/WebP 文件头识别格式并从头部解析宽高；单张 ≤10MB、宽或高 ≤8192、总像素 ≤4000 万，伪造扩展名、截断文件、非图片字节与像素炸弹均拒绝。落盘扩展名及接口返回的 `contentType` 以探测结果为准，发送模型前再次校验（防上传后篡改），展示文件名去除路径与控制字符。补充各格式头、像素炸弹、超 10MB 与非法字节回归测试。
- **Web 文件下载边界（P2-8）**：会话文件下载增加单文件 100 MiB 上限（超限 413 + `file_download_too_large`）、单段 `Range`/206/416、每用户 4 并发（超限 429 + `file_download_limited`）与 5 分钟响应超时；始终流式发送，不整文件读入内存。补 Range、超限与并发租约测试。
- **Web 登录与传输硬化（P2-7）**：登录在每 IP 30 次/分钟之外，新增同账号跨 IP 10 次/分钟限速（防分布式口令喷洒，超限 429 + `login_rate_limited`）；新账号/改密最短密码由 6 位提高到 8 位；登录/登出/`me` 响应统一 `Cache-Control: no-store`；配置 HTTPS 证书后启用 HSTS 与 HTTP→HTTPS 重定向；新增 `web.trustedProxies`，非空时才信任列内代理的 `X-Forwarded-For/Proto`（空=忽略，防伪造来源 IP/协议）。管理员 RBAC 保持现有服务端能力边界（`allUsers`、共享工作区写、团队 Memory、全局拓扑），现有回归测试继续覆盖。
- **日志与规则脱敏（P2-5）**：新增 `SecretRedactor`，审计命令/SQL/结果、文件路径、终端 `.log/.cast` 落盘前不可逆掩码密码、token、API key、连接串、Cookie、URL userinfo、Bearer/Basic 凭据、私钥块与常见云密钥；命中敏感内容时审计结果追加 `[敏感内容已自动脱敏]`。AlwaysAllow 改为保存 `sha256:<hash> | <脱敏展示>`，旧明文规则加载时自动迁移，匹配仍按完整命令哈希；补脱敏/迁移/审计链/终端日志回归测试。
- **配置并发写入治理（P2-6）**：`ConfigService.SaveConfigAsync` 增加进程内串行 + 跨进程 `config.json.lock`；按加载时快照与磁盘最新配置做三路合并，不同字段并发修改自动保留，同一字段/数组双方都改则抛 `ConfigConflictException`，不静默覆盖。保留临时文件原子替换，补并发合并与同字段冲突测试。
- **内部工具可见性与文档一致性（P3-1/P3-2）**：`mcp_approval_round` 改为仅 LitSSH 宿主注入 `LITSSH_INTERNAL_TOOLS=1` 时注册，外部 MCP 客户端 `tools/list` 默认 60 个、`mcp_usage_guide` 也隐藏该内部工具；修正 README 的 SignalR 残留描述，并统一“60 外部 + 1 内部工具”的文档口径。
- **App 异步边界与窗口生命周期（P3-3 阶段 1）**：新增 `AsyncOperation` 与统一 `App.ReportError`，快照历史/趋势、拓扑时间线、AI 助手窗口的 Loaded/刷新/切换/关闭路径改用集中异常边界；窗口关闭时解绑 Loaded；错误日志与去重提示复用同一入口。剩余 ViewModel `async void` 继续分批迁移。
- **App 异步生命周期收口（P3-3 完成）**：全部窗口事件与 ViewModel 的加载/保存/删除/连接/导出等操作迁移到 `AsyncOperation`/`Task`，App 源码已无 `async void`；异常统一进入 `App.ReportError` 的日志与去重提示。新增源码扫描测试防止回退。
- **应用锁窗口支持最小化**：`ResizeMode` 改为 `CanMinimize` 并恢复标题栏最小化按钮，暂时不想解锁时可最小化到任务栏，锁定状态与 relock 关闭拦截保持不变；补 XAML/ResizeMode 回归断言。
- **MCP 工具收敛 Phase A1（文件传输）**：新增 `ssh_upload` / `ssh_download` 统一单/多文件与目录传输，删除旧 `ssh_upload_file` / `ssh_upload_files` / `ssh_download_file` / `ssh_download_files`；安全目录、使用指南与集成测试同步更新。
- **内嵌 VNC 回环服务加固（P2-4）**：VNC 密码与桥接 token 改放 URL fragment，页面脚本读取后立即 `history.replaceState` 清除，不再进入 HTTP 请求、Referer 或服务端日志；每个 `VncWebHost` 生成 256 位随机 token，`/websockify` 握手同时校验 token、`Origin` 与 `Host` 必须与当前 `127.0.0.1` 端口完全一致，缺失、异源或错误 token 一律 403，防止恶意网页/本机其他进程借用桥接访问 VNC。静态资源改用 `PathGuard.IsUnder` 的“根 + 分隔符”边界并解析链接，修复 `novnc-evil` 之类同前缀目录绕过，并补充 `nosniff` / `no-referrer` / `DENY` 响应头。补充 fragment 敏感信息、token/Origin 拒绝与静态资源回归测试。
- **WebSocket 握手、连接数与背压收口（P2-2）**：`/ws/sessions/{id}` 与 `/ws/approvals` 的 Origin 校验由“只比 Host”改为必须携带 Origin 且 scheme/host/port 与请求完全一致，缺失、`null`、带路径/查询/userinfo 或端口不符均 403；新增 WS 专用握手限速（默认每 IP 120 次/分钟）与每用户 8、每 IP 64 的并发连接上限，超限 429 并记录 `ws_rate_limited` / `ws_connection_limited`。聊天会话流式帧由无界 Channel 改为容量 256 的有界队列，慢客户端触发背压而非无限堆积；连接断开时泵关闭队列并丢弃后续事件，避免生产者永久阻塞。补充 Origin 策略、连接租约与缺失 Origin 集成测试。
- **审批豁免按目标与操作收口（P2-1）**：`ApprovalExemptionRegistry` 的授权键由“工具名”改为“工具 + 目标 + 完整命令/操作 + 文件路径”，因此在 A 服务器或某条命令上勾选的「本轮次 / 整个会话」放行，不会复用到 B 服务器或另一条命令；弹窗文案同步改为“同一目标与相同操作”，会话级授权提示仅显示“工具 @ 目标”。补充跨目标、跨命令与轮次隔离测试。
- **配置损坏时应用锁失败关闭（P1-5）**：`ConfigService` 保留普通 `LoadConfigAsync` 的容错降级供非安全入口使用，新增 `IStrictConfigService.LoadStrictAsync`；桌面启动门卫与托盘恢复改用严格读取，配置文件存在但损坏、无法解析或迁移失败时抛出 `ConfigLoadException`，显示错误并退出/保持隐藏，不再静默回落默认配置而被当作“未设置应用锁”绕过。首次启动缺少配置文件时仍正常创建默认配置；补充损坏配置与缺失配置回归测试。
- **跨机复制/服务器端同步恢复主机密钥校验（P1-6）**：源机到目标的 `ssh`/`rsync -e`/`tar | ssh` 不再使用 `StrictHostKeyChecking=no`，默认改为 `accept-new`（首次仍为 TOFU，记录后拒绝指纹变化）；源机探测新增 `ssh -G` 能力检查，旧版 OpenSSH 不支持 `accept-new` 时回退 `StrictHostKeyChecking=yes` 并提示目标须已记录在源机 `known_hosts`，避免为兼容老客户端重新关闭校验。跨机复制与服务器端 rsync 同步均使用探测到的模式，并补充“绝不生成 `StrictHostKeyChecking=no`”回归测试。
- **`docker_exec` 参数注入收口（P1-1）**：容器内命令不再原样拼进远端 `docker exec` 命令行，改为新增 `PosixCommandLine` 按 argv 拆分（支持单/双引号与反斜杠转义、不展开变量/通配符/管道/命令替换），再由 `ShellQuote.Join` 逐参数单引号转义；因此 `true; touch /tmp/pwn` 只会作为容器内进程参数处理，无法逃逸到宿主机 shell。需要管道等 shell 语法时必须显式 `sh -c '...'`；命令为空、引号未闭合、含换行/NUL 时返回 `invalid_argument`。补充 tokenizer 与 `docker_exec` 注入回归测试。
- **Markdown 链接协议收口（P1-7）**：桌面 AI 回答中的链接只保留 `http` / `https` / `mailto`；`file:`、`ms-msdt:`、`javascript:` 及其他未知 scheme 不再生成可点击链接，只显示原文并在悬浮提示中说明已阻止，避免提示注入借一键点击触发本地文件或系统协议处理器。
- **本机终端单命令边界与审批收紧（P0-4）**：`local_shell` / `local_shell_open` 在写入 PowerShell 前拒绝引号外的 `;`、`&&`、`||`、`|`、`&`、重定向、反引号、`$()`/`@()`、脚本块、括号/方括号表达式、换行与未闭合引号，并直接拒绝 `cmd`/`powershell`/`pwsh`/`bash`/`sh`/`zsh`/`wsl`/`cscript`/`wscript` 嵌套 shell；引号内普通文本（如提交信息中的 `|`）与行首 `&` 调用运算符不受影响。`local_shell` 的「总是允许」由 `rule + " "` 前缀匹配改为**完整命令行精确匹配**，`git status` 的授权不再覆盖 `git status --short`，更不会覆盖追加参数/注入片段的命令。默认 CLI 白名单移除 `python`/`python3`/`node`/`npx`/`pip`/`pip3`/`poetry`/`uv`/`pdm`/`conda`/`java` 等解释器/运行时，确需时由用户显式添加；构建工具与 `npm` 等仍保留（其脚本执行能力仍需逐次审批）。新增危险语法、引号误报、嵌套 shell、精确审批与默认白名单回归测试。
- **远端文件名与同步路径本地写入防护（P0-5）**：新增 `RemotePathGuard`，远端文件名只接受单段安全名称，拒绝 `/`、`\`、盘符/ADS、`..`、控制字符、Windows 保留设备名与尾随点/空格；临时下载路径先规范化再做 `PathGuard` 物理包含校验，远端→本地同步的相对路径按 POSIX 段校验、写入与 `DeleteExtra` 删除统一校验，批量下载同样拒绝越界/不安全名称。打开远端文件改用「打开方式」选择编辑器，不再对下载内容使用 `UseShellExecute` 直接启动；可执行/脚本类型先提示再交给用户选择程序，避免被远端文件名触发本地执行。
- **会话私有落盘、共享区写权限与记忆隔离（P0-3 部分）**：Web 会话的大结果落盘改为 `spills/<用户Id>/` 子目录隔离，`spill_list` / `spill_read` / `spill_grep` 只能读取当前用户自己的句柄；桌面未启用隔离时保持原目录行为。Web 普通成员的共享工作区改为只读：`ops_doc_write/append/patch` 的默认路径与 `shared/` 路径均拒绝写入，会话目录仍可写；`ops_script_register` 仅管理员可用。长期记忆新增 `OwnerUserId` / `Scope(team|private)` / `Version`（旧库自动迁移为团队记忆）：Web 成员读取“团队 + 自己私有”、只能写自己的私有，管理员写团队并保留团队维护权；`ops_memory_update/delete` 无法改动他人的私有记忆，近重复合并也不会改写只读团队记忆。`ops_doc_*` 的 `shared/` 路径先做字面归一化再判定会话树，修复 `shared/./jobs/...`、`shared//jobs/...`、反斜杠与大小写变体可绕过 `shared/jobs/` 隔离的问题；`ops_doc_list` 也不再列出共享区 `jobs/` 下的其他会话产物。
- **Web 授权目标覆盖与绕过修复**：日志/Docker/Java/服务/拓扑/应用体检等目标工具纳入统一授权目录，无法解析的目标改为 fail-closed；`ssh_execute_command` 不再用“首词只读”降低授权或写锁，任意 shell 都要求 exec；SQL `Sensitive` 不再算只读；`ssh_snapshot_refresh` 归入 exec 并占写锁。
- **会话日志 / 录像在录制中可查看**：会话日志窗口改为以 `FileShare.ReadWrite | FileShare.Delete` 读取 `.log` / `.cast`，不再因录制端持续持有写句柄而报「文件正被另一个进程使用」。这不是会话日志逻辑冲突——终端操作发送的命令回显和输出仍由读循环正常写入 `.log` / `.cast`，只是此前查看时被 Windows 文件共享规则拒绝；现在可一边执行大日志切片 / grep，一边打开会话日志查看当前进度，刷新列表即可看到新增内容。
- **关闭终端后剩余标签空白**：停靠标签改为增量同步，不再整表清空重建；关闭当前终端、浮动终端或删除 AI 会话时保留原选中索引，并在绑定把 `SelectedSession` 置空后延迟补选剩余会话，剩余标签内容立即正常显示，无需再点一下标签。
- **「测试连接」失败可诊断且与终端一致**：编辑服务器/会话的「测试连接」改用 `ProbeConnectionAsync`，状态栏区分认证失败 / 主机密钥不匹配 / 超时 / 网络不可达并附原始错误，不再只显示“连接失败”；探测超时由 10 秒对齐交互式终端的 30 秒（首次连接 DNS/KEX/主机密钥协商/VPN 冷启动常超过 10 秒）；主界面 SSH 服务、远程文件夹选择、文件同步（含调度器）统一改走 `AppServiceFactory.CreateSshService()` / `CreateSyncService()`，与数据源、端口转发、拓扑发现共用同一套 known-hosts 与 Tofu/Strict/Off 策略，不再绕过主机密钥校验。
- **Web 发送按钮恢复可用**：发送/停止合并按钮在 `close()` 被禁用后，`open()` 重新选中会话时未解禁，导致输入文字后鼠标点击无反应而 Enter 仍能发送；现在选中会话后按状态正确启用，运行中保持可点击以停止。
- **AI 会话上下文注入可回看**：记忆召回/资产图谱步骤改为随会话记录持久化（`IsInfo`），任务完成后的自动刷新、切换会话和重启不再丢失；仍不计入模型恢复轨迹，避免把注入内容伪装成工具结果。Web 历史与实时 `asset_graph_context` 事件同步展示。
- **Web 会话级 ToolPolicy 生效**：Web 运行时装配时读取 `agent_sessions.ToolPolicy` 并 `SetToolPolicy`，桌面配置的会话级工具允许/拒绝策略在 Web 端不再失效；损坏/空策略按无策略处理，不阻断会话启动。
- **Web 外部连接器下发白名单可视化**：桌面「设置 → 安全设置 → Web 授权矩阵」新增“允许下发 Web 的外部连接器”多选（来自 `agent.mcpServers` 非内置条目；无外部连接器时显示引导文案），保存到 `security.authorization.webConnectors`；之前只能手改 `config.json`。App 端补嵌入文档回归测试，确保 5 个图查询工具出现在「MCP 工具说明」。

## [1.4.0] - 2026-10-18

### 新增

- **桌面可选登录（D1 收尾，仅归因）**：AI 助手窗口侧栏显示归因状态并提供登录 / 注销；复用 Web 账号库 `web.db` 校验（失败计数与锁定曲线与 Web 共享，防绕过限速），登录态经 DPAPI 加密落盘 `desktop-login.dat` **跨重启保持**（账号删除 / 文件被篡改自动失效）。登录后**新会话 owner 与 App 审计 UserId** 记为登录用户名，未登录继续归因 `local`；桌面不因此获得或失去任何权限（不注入 `LITSSH_AUTHZ_MODE`，D1「仅归因不限权」）。
- **Web 会话工作区根可配（§8.3）**：`config.json` 新增 `web.jobsRoot`——Web 会话目录固定根（空=默认 `%APPDATA%\LitSSH\jobs`，仅接受绝对路径，非法值回退默认，**重启 Web 生效**）；会话目录为 `<根>\<用户Id>\s<会话Id>`，文件浏览 / 下载 / 删除的"必须位于固定根之下"校验随新根生效。桌面「设置 → 安全设置」新增**可视化编辑框**（含「恢复默认」）。
- **孤儿会话目录清扫（§8.2 附件保留策略）**：新增 `agent.orphanSessionDirRetentionDays`（默认 30，0=关闭）——会话删除仍立即清理目录，另对"库中已无对应会话"的残留 `jobs/<用户>/s<id>`（删除失败 / 被会话保留策略剪除）按最后写入时间超期自动清理；桌面启动（裁剪后）与 Web 启动各清扫一次，跳过重解析点、保护未超期目录，清空后的 owner 目录一并删除。
- **审计格式 v5→v6 保留数据迁移**：升级时不再清空审计库——为四张审计表补 `UserId` 列（旧记录为空，界面显示 `local`），并按含 UserId 的新 payload **重建哈希链**（链从创世哈希重新开始，历史记录/归档全部保留）；**更老版本（<5）同样保留记录**：启动时补齐 `SessionId`/`Tool`/`Category`/`Decision`/`UserId` 等缺失列后按 v6 payload 重建链，不再清理审计库。
- **会话历史分页 / 懒加载**：`GET /api/sessions/{id}/messages?limit=&beforeId=`（默认最近 120 条，升序返回）；Web 首屏只加载最近一页，顶部「加载更早的记录」按需向前翻页并保持滚动位置，长会话不再一次性拉全量/渲染全量 DOM。
- **Web 冷启动上下文与桌面对齐**：抽出共享 `SessionContextBuilder` + `ToolTraceFormatter`（桌面端复用同一轨迹格式器，避免两端漂移）——按 `agent.coldStartRecentTurns` 裁剪、每轮注入工具轨迹 System 证据、视觉模型重载最近 3 张图片，并把**会话智能体 / 激活技能 / 计划模式 / 资产图谱**一并注入。同一会话在两端喂给模型的上下文一致，跨端切换时前缀缓存可最大化复用。残余差异：Web 仍不含本机终端工具（安全裁剪），桌面开启该工具组时工具清单会导致前后缀分歧。
- **Web 图片全屏预览**：点击聊天气泡附图 / 历史缩略图 / Markdown 图片即可打开**全屏查看器**（深色遮罩、按比例适配，点击图片/遮罩/关闭按钮或 Esc 退出）。
- **Web 聊天 UI/UX 重做（响应式，手机可用）**：顶栏新增连接状态点 / 待审批徽标；**输入区改为桌面风格——发送/停止合并为一个按钮（运行中点击即停止）**，大模型选择与图片上传放在**输入框下方同一工具行**；会话列居中（桌面）与**抽屉式侧栏**（≤820px）；**固定底部输入栏**（自动增高、安全区适配、手机上 Enter 换行、发送按钮提交）；「回到最新」悬浮按钮（向上翻历史时不强制滚底）；会话相对时间、首条消息自动命名；代码块一键复制、GFM 表格 / 任务列表渲染；流式输出按帧节流渲染。
- **Web 图片附件**：输入区「图片」按钮 / 粘贴 / 拖拽（png/jpeg/webp/gif，单张 ≤10MB、每条 ≤5 张）；先上传到会话目录 `attachments/`（服务端生成文件名），再随消息以**多模态内容**发给模型；非图片/超限拒绝。
- **Web 会话级模型切换**：`GET /api/agent/providers`（仅启用模型）+ `PUT /api/sessions/{id}/provider`（下一轮生效；运行中 409）；顶栏展示当前模型并可在弹层切换，写入 `agent_sessions.ProviderId`。
- **审计日志展示用户归因**：命令 / SQL 两张审计表新增「用户」列（`UserId`，空=桌面未登录的 `local`），CSV 导出同步；关键词搜索覆盖 `UserId`（该列 v6 已并入哈希链，此前 UI/导出未展示）。Web 会话审计存的是**用户 Id**（稳定标识，审批路由也按 Id），界面/CSV 会按 `web.db` 映射**显示为用户名**（如 `1` → `admin`；账号已删除/无账号库时回退原 Id），按用户名搜索自动映射为 Id 过滤。
- **M0 打底（安全与并行前置）**：审计哈希链**跨进程锁**——新增 `CrossProcessFileLock`（`FileShare.None` 独占锁文件，进程崩溃由 OS 释放句柄自愈），链追加"读 prev → 计算 → 插入"与链库初始化全程跨进程串行（**并行子进程并发追加不再链分叉**；超时宁可写失败也不破链，`LockTimeout` 默认 30s；写连接 `busy_timeout` 放宽防 SQLITE_BUSY 丢审计）+ `AuditContext.CurrentUserId` AsyncLocal 槽位（M0 预留、M1 进链 payload 防二次链重建）；打底项：`agent.db` 每连接 `busy_timeout=3000`、`SubAgents.taskCounts` 改 `ConcurrentDictionary`、`ChatClientFactory` 按端点+密钥哈希+模型缓存 OpenAIClient（修每次新建从不 Dispose 的句柄泄漏）；审批链路：超时钟改为**弹窗可见后起算**（排队中的后续审批不再沿用发起时刻误判超时）、本机终端审批改 `BeginInvoke + TaskCompletionSource` 异步化（不再同步阻塞 UI 线程）、桌面/子进程审批弹窗加**会话标识**（并行后多弹窗并排防错认）；附 `AuditChainConcurrencyTests`/`CrossProcessFileLockTests` 并发回归。
- **Web 助手（`LitSSHmcp.Web`，全新浏览器端）**：Kestrel 宿主 + `wwwroot` 零构建链静态 SPA（登录/会话列表/流式聊天/审批卡片/目录浏览下载）。独立账号库 `web.db`（PBKDF2-SHA256 210k 迭代、连续失败 5 次起指数锁定、首账号自动 admin）；账号管理 CLI `--create-user <名> [--role admin|user]` / `--list-users`（密码只经 `LITSSH_NEW_PASSWORD` 环境变量，不进 argv/进程列表）；每个 AI 会话运行在**独立 AgentRuntime 子进程**（stdio MCP 子进程、会话 owner 逐操作校验、删除会话即终止）；WebSocket 流式聊天（assistant_text/reasoning/tool/tool_result/done/error 分帧、busy 409、掉线取消）与 `/ws/approvals` 审批实时推送（桥接 cli 文件通道，REST 决策吃 CSRF）；会话目录文件浏览/下载（owner + `..` 路径防护）；模型输出 Markdown 经白名单 sanitize 后渲染（构造性 XSS 防护，全部 escape 后仅产白名单标签/URL scheme）。
- **Web 安全栈**：登录与 API 按 IP 固定窗口限速（`/api/login` 30 次/分、`/api/*` 600 次/分，超限 429 + `rate_limited` 日志）；每用户并发会话上限 `security.limits.maxSessionsPerUser`（默认 8，超限 409 + `session_limit_reached`）；全站安全响应头（`nosniff`/`X-Frame-Options: DENY`/`Referrer-Policy`/`Permissions-Policy` + HTML CSP，与 index.html meta 双保险）；LLM 成本预算 Web 端发送前拦截（`WebBudgetGuard` 对齐桌面会话预算，会话数 × 成本上限 = 单用户成本上界）；HTTPS 启动（`config.json` `web.https` PFX 证书追加监听，默认 7443 绑定所有网卡，密码 `enc:` DPAPI 密文，读配置失败不阻断启动）；访问日志覆盖登录成败/会话创建删除/文件下载/越权与限速拒绝。
- **操作用户归因进审计链（AuditFormatVersion v6）**：审计四表同序追加 `UserId` 列并纳入哈希链（防篡改检出）；归因解析显式 > 宿主上下文 > `local`；MCP 子进程由宿主 spawn 注入 `LITSSH_OWNER_USER_ID` 环境变量防伪造。
- **两级工作区与会话级工作目录**：`agent.db` schema v5（会话表 `WorkspaceDir`/`OwnerUserId`）；工作区 `jobs/<owner>/s<id>` 布局 + 目录名段消毒；双根模式（会话目录默认落盘、`shared/` 共享区提升）+ 系统提示注入本会话工作目录取值；fileTransfer 会话目录条件注入（越界不注入防路径洗白）与下载默认落地会话目录。
- **路径与本机工具加固**：`PathGuard` 逐段解析 junction/符号链接到物理路径再判前缀归属（区内链接放行、指向区外拒绝）；`LocalAudit` 统一出口（`Local` 审计类别：`ops_doc_write` 三类变更、脚本台账、附件落盘、会话删除、本机终端策略/拒绝/执行全部入链，测试 `OverrideSink` 替身防触真实 audit.db）；`ops_promote` 提升命令（会话目录→共享区，脚本类强制桌面确认 + 来源会话标注、目标存在/路径穿越/shared 前缀一律拒）+ 上传源限制在会话目录内 + 会话目录随删除清理；脚本跨根直写共享区自动注入**未审阅标记**（堵提升旁路）。
- **跨进程写互斥与限流共享（M2-1）**：`TargetLimiter` 增加跨进程层（共享槽位文件 + 分钟速率窗口 `rate.lock`/`rate.json`，进程崩溃自愈、锁竞争失败安全降级仅进程内）；`FileTargetWriteMutex` 同一目标跨进程同一时刻仅一个写类任务（等待上限 5s，超时 `write_busy`，锁目录不可用一律拒 fail-closed）；`McpWriteGateFilter` 写类工具请求门（常写清单 + 条件写按参数判只读，shell 首词白名单宁严勿松）。
- **按会话隔离的运行状态机（M2-2/M2-3/M2-4）**：`SessionRunContext`（Queued/Running/WaitingApproval/Done/Failed/Stopped + 独立 runtime + 权威轮次/计划镜像），移除切换会话「任务中禁切」闸、切换/删除/归档/关窗全路径按池元素收口，运行收尾/落库/摘要闭包烧定会话上下文防串会话；停止与审批按会话路由 + 侧栏会话状态徽标；会话级连接器/技能子集启用层（入口拆分）；`AgentSession.SendAsync` 重入硬闸。
- **授权矩阵强制与连接器白名单收窄（M3-4）**：用户→目标白名单 + read/exec 操作级别落工具过滤器（Web 宿主 spawn 的 MCP 子进程经 `HostEnv` 强制执行，桌面进程仅归因不限权）。
- **Web 授权矩阵可视化配置**：桌面「设置 → 安全设置 → **Web 授权矩阵**」新增启用开关与规则行编辑（用户名 / 目标白名单 `ssh:<id>`·`ds:<id>`·`ssh:*`·`*` / read|exec 级别，可增删行），保存 `security.authorization` 热生效——此前该矩阵只有模型与强制过滤器、无任何编辑入口，Web 用户默认 fail-closed 全拒时只能手改 `config.json`；映射纯逻辑与 XAML 结构均有针对性测试。

### 修复

- **Web 历史图片缩略图回放**：图片附件落库改用与桌面端一致的 `🖼 图片: 名称 [相对路径]` 标记；Web 历史解析标记后经下载接口内联缩略图（仅接受会话 `attachments/` 下路径），模型历史转为「（附图：名称）」文字提示（不重复附图、保留语义）。
- **Web 模型选择弹层位置**：桌面端改为贴着输入区「模型」按钮**上方弹出**（不再固定右上角），窗口缩放时重新定位；移动端保持底部弹层。
- **Web 结论丢帧自愈（改为权威视图）**：`done` 后不再信任流式累计文本，**无条件按落库内容重建**当前会话（服务端在 done 前已落库），同时把实时工具卡片替换为带结果的历史卡片——彻底避免 WS 丢帧导致的"结论内容不全"。
- **Web 静态资源 no-cache + 移除 meta 中无效 CSP**：`/js`、`/css` 等静态资源加 `Cache-Control: no-cache`（ETag/304 协商），升级后不再出现浏览器沿用旧脚本的"修复不生效"；`index.html` meta CSP 去掉 `<meta>` 中无效的 `frame-ancestors`（仍由响应头 `X-Frame-Options: DENY` + CSP 头强制），消除控制台告警。
- **Web 发送后最终结论偶发不显示（末尾空气泡）**：流式渲染按帧节流，`done` 到达即 `resetRun()` 清空 `assistantText`；若最后一段 `assistant_text` 的渲染仍在排队，就会读到空文本 → 结论被清成空气泡。修复：排队渲染**捕获文本快照** + `done`/`error` 前**同步 flush** 最后一段输出；附 node 回归用例。
- **Web 历史工具轨迹显示为转义 JSON**：桌面端持久化工具步骤原用默认 JSON 编码器（内层引号 → `\u0022`、中文 → `\uXXXX`），Web 又把 `role="tool"` 历史行当助手正文渲染——改为 relaxed 编码落库，且 Web 历史工具行渲染为**可折叠卡片**（工具/参数/结果/耗时，兼容旧转义行）。
- **Web 审批操作区不易发现**：审批卡片原为聊天区顶部条带（长会话历史下需回滚到顶部才注意到）——改为**固定悬浮面板**（右上角、不随聊天滚动、可收起；新审批到达自动展开）+ 顶栏「待审批 N」徽标（点击展开）。
- **Web 审批/聊天实时性缺口（二次审批不出现/结果需刷新）**：审批推送此前只靠 WS 增量——补 **REST 对账**（启动、每 5s、每次决策后、WS 重连/断线时拉 `GET /api/approvals` 全量对齐，漏推自愈）；聊天 WS **重连后重放已落库历史**，断线时若回合仍在执行则轮询库内消息直至结果落库（最长 2 分钟）；服务端回合改为**后台执行**、接收循环不再被 `await` 阻塞（原实现的 `stop` 帧要等回合结束才被处理）。
- **桌面看不到 Web 进程写入的会话进度**：Web 为独立进程、桌面无跨进程感知——新增 **5s 轻量对账**（`SessionAutoRefresh`）：会话列表快照变化即重载列表；当前会话空闲且 `UpdatedAt` 变化时重载该会话消息（本地运行中/编辑中跳过）。**限制**：跨进程只能同步**已落库的整轮结果**（Web 在回合结束才写消息），无法实时流式。
- **Web 直跑 exe 静态资源 404**：内容根固定为 `AppContext.BaseDirectory`（原随启动 CWD 漂移，仓库根/服务方式启动时 WebRootPath 落到不存在的 `<cwd>\wwwroot`）；非 Development 不加载 static web assets manifest 而 bin 下无 `wwwroot`，改为 csproj 把 `wwwroot\**` 复制到输出（Development 仍经 manifest 读项目目录）。
- **登录成功后仍停留登录窗**：`#login-view{display:flex}` 与 `.strip{display:flex}` 的作者样式**压过 UA 对 `[hidden]` 的 `display:none`**，`showApp()` 设 `hidden` 后登录窗藏不住（主界面其实已渲染在下方）；`app.css` 增加全局 `[hidden] { display: none !important; }` 守卫 + 静态资源测试断言防回归（`#files-pane` 此前已有局部防护，同类坑补齐）。
- **Web 账号管理命令行未接线（M3-1 缺口）**：`WebBootstrap` 的 `--create-user`/`--list-users` 自 M3-2 起从未被 `Program` 调用，Web 一度无任何途径创建登录账号——启动前 `RunCliAsync` 分派，未命中则透传宿主参数。
- **Web 会话目录重复套 `jobs`（布局与 §8.3 不符）**：`WebJobsRoot`（`%APPDATA%\LitSSH\jobs`）被当作共享根又拼一次 `jobs`，实际落盘 `jobs\jobs\<用户>\s<会话>`——改为固定根下直接 `<用户Id>\s<会话Id>`；启动时自动迁移旧路径的库行与目录（移动失败保留旧行下次重试，幂等）。
- **App 测试偶发串扰**：`LocalApprovalScopeTests` 未标 `[Collection("local-audit")]`，与 `PromoteToolTests` 并行时经进程级静态 `LocalAudit.OverrideSink` 把本机终端审计记录写进对方断言快照——补同集合串行标注（与 `LocalTerminalToolTests` 先例一致）。
- **登录后聊天输入框无法输入且无提示**：`chat.open()` 先 `close()`（无会话态强制禁用输入/发送）再设 `sessionId`，但设置后从未重新启用，WS 打开也只改状态文字（显示"就绪"却不解禁）——选中会话也发不出第一条消息，永远等不到 `done`/`error` 事件解禁；`open()` 末尾按已选会话 `setBusy(false)`，未选会话时 placeholder 明示「先点『＋ 新会话』」；node 真跑交付文件的 `chat-open.test.mjs` 防回归。

## [1.3.0] - 2026-10-17

### 修复

- **数据库工作台 · 刷新不再丢当前数据源 / 库**：点「刷新」时顶部数据源下拉曾被 `Datasources.Clear()` 瞬时回写成 `null`（连带把库列表与对象树清掉）——改为保留最近有效数据源（`_lastDatasourceId` 判断是否真切换），同实例只补加载、真正切换才重置所选库与 Redis DB；库列表加载完成后优先恢复刷新前选中的库（`_preferredDatabase`）。
- **Redis 查询报错后「双击键 / 查看值」看起来没反应**：查询失败分支会清空结果子标签，重建后只剩「消息」标签并被自动选中（结果网格隐藏、`Results` 置 null）；读键此前只写 `Results` 不切换子标签，键值被错误消息面板盖住、错误看似"清不掉"。现读键后重建子标签（`RebuildKeyResultTabs`）：成功建「结果（键值）」并选中，失败选中「消息」展示原因，消息历史仍可手动切回。
- **AI 助手设置改为非模态**：「AI 助手设置」窗口此前用 `ShowDialog` 模态打开（锁死交互，无法同时点 AI 助手与主程序页面），现改为**非模态单实例**（`Show()` + 已打开时前置激活并可切换到请求的 Tab）；Owner 仍为 AI 助手窗口（居中其上、随其关闭），关闭后照常重载 provider/会话/提示词模板。
- **AI 助手设置 · Embeddings API Key 圆点显示**：长期记忆区的 Embeddings API Key 由明文 `TextBox` 改为 `PasswordBox`（`PasswordChar=●`，与大模型设置的 API Key 一致），加载/重新加载自动回填、输入即回写 VM。

### 新增

- **AI 助手 · OpenAI `prompt_cache_key` 注入（仅官方端点）**：对 **api.openai.com（含空 endpoint=SDK 默认）** 的请求，经 `Microsoft.Extensions.AI` 的 `RawRepresentationFactory` + OpenAI SDK 官方 `ChatCompletionOptions.Patch` 注入稳定的 `prompt_cache_key`（`provider.Id+Model` 的 SHA256 短哈希，前缀 `litssh-`），让 OpenAI 自动前缀缓存在**同模型跨会话**间也稳定命中（默认只按前缀匹配，key 把不同会话/租户显式分组）；调用方自带 factory 时不覆盖、无 key 时不动 options；**非官方端点（DeepSeek/中转/Ollama 等）一律不注入**（它们不识别该字段）。`ResilientChatClient` 在非流式/流式两个入口统一套用。
- **AI 助手 · 资产图谱注入（拓扑快照）**：会话启动时把配置中的服务器/数据源/应用/关系渲染为紧凑文本（`AssetGraphPromptFormatter`，纯函数、无时间戳不打散前缀缓存、≤4000 字符超限按「应用细节→关系→截断」降级、禁用服务器自动排除），以 `## 资产清单(拓扑快照)` 段注入系统提示（插在 MCP 服务器约定之前）——模型开局即知 `ssh:`/`ds:`/`app:` ID、主机:端口、隧道/路径/关系，无需先调列表工具；**仅 topology 工具组启用时注入**（停用则整段省略）。
- **AI 助手 · 记忆质量门（四层）**：①系统提示新增内容质量规则（不记一次性问答、日志只记应用/路径+关键行≤3行、结构=现象→根因→处置→验证、**assetIds 必填**、**凭据绝不入库**）；②`ops_memory_save` 写入门禁——**敏感词拒绝**（password/api_key/token/PEM 私钥/AWS/GitHub/OpenAI 形态等正则）、**过短拒绝**（<8 字符）、**近重复合并**（与既有同 kind 任意条目余弦 >0.92 时挂到既有条目频次+1，不再新增 key）；③raw 会话片段质量门——**仅成功轮入库**（取消/失败轮不污染召回）、日志型超长输入压缩为「路径线索+助手结论≤200字」、普通轮截断 4000 字符、同源(`会话`)保留上限 200 条（超出自动删最旧）；④召回重排——**结构化经验优先**、raw 最多占 `min(2, topK)` 席、阈值可配（`agent.memory.recallThreshold`，默认 0.15）。

- **AI 运维助手 · 结构化经验记忆（`ops_memory_*`）**：`agent.db` 的 `agent_memory` 表扩展结构化列（`kind`/`key`/`assetIds`/`tags`/`freq`/`updatedAt`/`status`，旧库 `PRAGMA table_info` 平滑迁移）；新增 4 个可开关工具——`ops_memory_save`（同 kind+key 合并=覆盖最新路径、频次+1、资产并集）、`ops_memory_update`（修正内容或标 `superseded` 退役）、`ops_memory_list`（按 kind/资产过滤）、`ops_memory_delete`；类别 `task`（任务复盘）/`script`（脚本）/`asset`（资产经验）。**每轮用户消息自动召回**相关记忆注入上下文（`[kind:key ×freq]` 格式，`×freq` 越高=越多环境验证过，`superseded` 不再出现）；系统提示固化「先查后做 / 成功后记 / 保持更新」。开关 `agent.memory.enabled`（关闭=不注册工具、不注入提示段，向量检索基于本地 SQLite 余弦，数据不出本机）。
- **可复用脚本沉淀（`scripts/` + `ops_script_register`）**：工作区约定 `scripts/local/`（本机脚本）、`scripts/remote/`（服务器脚本）、`scripts/MANIFEST.md`（台账：路径/用途/兼容性/已部署资产/哈希）；`ops_script_register` 登记台账并同步 `kind=script` 记忆（重复登记合并已部署资产）。系统提示引导「先查再写 / 预判沉淀 / 兼容性必须声明（python2.7 vs 3.x、POSIX sh vs bash4+）」；新技能 `script-deploy-verify`（生成→上传→执行→验证→清理闭环 + 解释器兼容性探测），`litssh-mcp-ops-skill` 增加「经验记忆与脚本复用」章节。
- **脚本安全门（服务端权威，脚本永不免审）**：新增 `ScriptRiskAnalyzer`（逐行复用命令过滤 Blocked/Sensitive 规则 + 脚本专用规则：fork 炸弹、`curl|sh` 管道执行、写裸盘、反弹 shell、SSH 凭据、`/etc` 账号文件、清历史、crontab、`base64|sh`、`eval`、chmod 777 等）与 `ScriptExecForm`（识别 `bash x.sh` / `python3 a.py` / `powershell -File` / `./x.sh`；`bash -c` 内联不走脚本门）。**执行侧**（`ssh_execute_command`/`ssh_execute_sudo`）：执行前 `cat` 预读远端脚本内容扫描（写 `category=probe` 审计），命中危险模式**直接 `status=blocked` 不进审批**（`reason=blocked_script`），扫描报告（结论/命中行/sha256/助手评估）附审批弹窗，**脚本形态一律人工同意**（即使命令行本身无敏感词）。**上传侧**（`ssh_upload_file`/`ssh_upload_files`）：本地内容扫描，单文件命中 `status=blocked_script` 硬拒，批量聚合**任一脚本危险整批拒绝**。三个工具新增可选 `riskNote` 参数（助手评估摘要，展示标注「仅供参考，服务端扫描才是权威」）；审批接口新增 `riskReport` 展示参数（桌面弹窗与 CLI 通道统一拼接，不影响豁免判定）。
- **AI 助手设置 · 长期记忆区增强**：新增**「测试 Embeddings」**按钮（用当前页端点/模型/Key 即时发起一次向量化请求，**无需先保存**；Key 留空复用当前启用对话模型的 Key，与运行时回退逻辑一致；成功显示模型/维度/耗时，失败显示可读错误）与**「查看记忆统计」**按钮（读本机 `agent.db`：总条数、按类别分布、已过时条数、库文件大小与路径；库不存在提示「记忆库为空」）；标题文案澄清为「远程 Embeddings 向量化，向量存本机 SQLite 并本地检索，可选」（向量化是远程 API，本地只做存储与余弦检索）。
- **AI 助手设置 · 记忆管理标签页**：新增**「记忆管理」Tab**——列出 `agent.db` 记忆库全部条目（含 `kind=raw` 的会话/文档片段；显示 `[kind:key] ×频次 · 状态`、内容预览、更新时间/资产/标签，悬浮看全文），支持**按类别过滤**（全部/task/script/asset/raw，切换即时刷新）、**删除选中**（单次确认，含预览防删错）与**「清空全部记忆」**（**两次独立 MessageBox 二次确认**，用于更换 Embeddings 模型/向量维度前清空旧向量——旧维度向量无法被新模型检索）。切到该 Tab 自动刷新；设置窗口 `SelectTab("memory")` 可直达。
- **AI 助手 · 记忆召回可见性**：每轮自动召回的记忆此前只作为 System 消息静默注入上下文、界面不可见，现经 `AgentEventKind.MemoryRecall` 事件推送到 UI——本轮工具过程列表顶部出现**「记忆召回」信息步骤**（标注「已注入本轮上下文」，可展开看完整召回文本；**不参与重试、不落库为工具轨迹、不进模型恢复轨迹**），用户可直观看到"AI 这轮用了哪些历史经验"，配合记忆管理 Tab 可发现并删除错误经验。顺带：`RecallAsync` 增加可选 `kind` 过滤参数（默认 null=全部类别），结构化记忆带资产时召回标注资产 Id。
- **脚本风险规则可配置（`security.scriptRules`）**：脚本专用内置规则（fork炸弹/`curl|sh`/反弹shell/eval 等 15 条）不再硬编码——新增配置节点：`enabled`（整体开关，命令过滤扫描不受影响）、`disabledBuiltinRuleLabels`（按内置标签精确禁用单条）、`extraBlockedPatterns`/`extraSensitivePatterns`（追加自定义危险/可疑正则，非法正则自动跳过不崩溃）。「设置 → 安全设置」新增**「脚本风险规则」**编辑区（开关 + 两个追加正则框 + 禁用标签框 + 内置标签全集提示），保存后按配置文件 mtime **热生效**，无需重启 MCP。
- **AI 助手 · 运行预算上限（`agent.budget`）**：新增两道防失控烧钱闸门——①**单轮墙钟预算**（`maxTurnSeconds`，0=不限）：一条用户消息的完整工具循环每轮模型调用前检查 `Stopwatch`，超限发 Error 事件停止本轮并提示（可在 AI 助手设置调整）；②**会话成本上限**（`maxSessionCost`，0=不限）：发送前按所选模型入/出价计算会话累计估算成本，达到上限**阻止发送**并提示（仅当模型已填价格时可判定，未填价格/未配置则放行）。「AI 助手设置 → 上下文与输出」新增两个输入框；`config.example.json` 补 `budget` 节点。
- **审批弹窗风险报告分栏**：桌面自绘审批弹窗（`dialog` 进程内样式）不再把脚本风险报告拼接在命令文本后，改为**左右分栏**——左栏命令原文（复制按钮只复制命令），右栏「脚本风险分析（服务端规则扫描）」独立滚动区；有报告时弹窗自动加宽（860px）。原生/子进程审批通道保持拼接文本；`ApprovalChannels` 调用链不变。
- **运维场景深度 runbook 技能包（5 份 `references/`）**：为高频场景补充按需读取的深度 runbook（`skill_read`，不进系统提示、零固定 token）——`mysql-troubleshoot/references/slow-query-runbook.md`（慢日志/digest 定位、`mysql_explain` 归因、处置与验证闭环）、`redis-troubleshoot/references/memory-runbook.md`（增长速率量化、大key/过期键/碎片归因）、`disk-full/references/inspection-runbook.md` 与 `ssl-cert-expiry/references/inspection-runbook.md`（周期巡检：批量采集、阈值分级报告、对比预警）、`deploy-rollback/references/ci-cd-rollback-runbook.md`（决策门槛、按发布形态回滚对照表）；对应 SKILL.md 加「深入」指引行，核心正文保持精简。
- **数据库工作台 · Redis 键浏览（百万级键不卡死）**：分批 `SCAN`（每批 500、单次 **2 秒时间预算** + 20 批上限，超预算自动停、可「继续扫描」游标续扫），`DBSIZE` 显示「已加载 N / 总数」进度；键值**有界读取**（hash/set 走 HSCAN/SSCAN、string 超 64KB 截断，长集合不再一次拉爆）；**补全类型 / TTL / 大小**（每批 50 键、10 秒预算，可反复点击续跑）；本地过滤（关键字 + 类型下拉）与列头排序（`GridViewSort`）；顶部新增 **Redis DB 下拉**（与数据源并排，切库即生效）；键右键菜单（查看值 / 复制键名 / 生成 GET 到输入框 / 删除键走写审批 + 审计）；只读模式对**大结果集命令**执行前警告确认（`RedisCommandPolicy.IsBulkRisk`：KEYS / HGETALL / SMEMBERS / 无界 RANGE 等，`-5 -1` 这类两端负下标有界切片不误报；范围语义与命令分类解耦，`Classify` 语义不变）。
- **MCP · Redis 连接复用 + `db` 参数**：新增 `RedisSessionCache`（`src/LitSSHmcp.Core/.../RedisSessionCache.cs`，App 与 MCP 共用）——同一 **数据源 + DB** 复用一条共享连接、空闲 30 秒自动释放、命令异常即作废重建；`datasource_test_connection` / 健康检查仍走独立连接互不影响。`redis_read` / `redis_execute` / `redis_diagnostics` 新增可选 **`db`**（0..63，越界返回 `status:"invalid_db"` 不落连接），返回结构回显实际 `db`；工具内改经 `session.ExecuteAsync`，默认跟随数据源库——跨库诊断不再需要先发 SELECT。`docs/TOOLS.md` 与 `mcp_usage_guide` 同步。
- **数据库工作台 · 对象树体验**：对象节点按类型显示**图标**（文件夹 / 表 / 视图 / 存储过程 / 列 / 索引 / 外键）；新增**选中对象条**（`库 · 表名 · N 列 · ~行数` + 备注行 + 「预览」「生成 SELECT」快捷按钮）；对象搜索改为**前缀命中优先排序**（输 `user` 时 `user_*` 排最前）并显示「匹配 x / y」计数；列节点两段式显示（列名 + 灰色 `类型 · PK · NOT NULL`），表 / 列**备注统一改为悬浮提示**（树内只显表名，不再被 ` · 备注` 撑乱）；对象树右键新增「刷新对象」「复制对象名」（分类文件夹同样可右键刷新，列 / 索引 / 外键行不弹菜单）。

### 新增

- **本机终端审批三档放行范围（全部命令级）**：`local_shell` 的「本机命令审批」弹窗对齐 MCP 审批——新增**放行范围勾选**（默认仅允许一次；「**本轮次内允许全部命令（不建议）**」到下一条用户消息自动失效、恢复正常审批；「**本次 AI 会话期间允许全部命令（不建议）**」到本会话窗口关闭为止，两档互斥）。因 PowerShell 命令常带大量参数/代码、按命令原文匹配不现实，范围按**全部命令**登记：勾选后本机终端的后续命令（须仍在 CLI 白名单内、危险拦截仍生效）一律不再逐条弹窗；已有会话级授权状态显示在弹窗提示区；「总是允许此命令」按钮保持不变（仍写入配置永久记忆）。实现：进程内 `LocalApprovalExemptionRegistry`（会话级/轮次级全放行，随 AI 会话窗口生命周期）+ `ApprovalRoundContext`（Agent 每条用户消息更新的进程内轮次，与跨进程 `mcp_approval_round` 同源）。
- **审批弹窗三档放行范围 + UI 重设计**：桌面审批弹窗（`dialog` / `process` 样式）新增**放行范围勾选**——默认**仅允许一次**（不变）；勾选「**本轮次内自动放行该工具**」后，同一条用户消息内的后续同工具调用自动放行，**发送下一条消息即自动失效、恢复正常审批**；勾选「**整个助手运行期间自动放行该工具（不建议）**」则到本次 MCP 会话结束（关闭助手 / 断开连接即失效）为止；两档互斥。实现：服务端 `ApprovalExemptionRegistry`（按 工具名×轮次 / 工具名×会话 登记，审批入口**预检命中即自动放行**，所有工具调用点零改动）+ 内部工具 `mcp_approval_round`（Agent 每条用户消息开始时通报新轮次 ID，**不暴露给模型**；轮次信号缺失只会多弹窗、绝不误放行）；`process` 子进程样式用退出码 3/4 回传范围；`native` 样式保持仅单次。弹窗 UI 重排：标题栏带服务器名、操作类型徽章、等宽命令框（复制按钮贴命令区）、范围勾选区（会话档红色警示 + 已有会话级授权清单提示）、倒计时，Enter=拒绝保险丝保留。
- **应用锁 · 托盘恢复解锁 + 退出行为修正**：①**双击托盘图标恢复主界面前**，若已启用应用锁则先弹模态解锁窗口（密码 / Windows Hello，不可点 X 绕过），解锁后才显示主界面（未设密码直接恢复；主界面本就可见时只激活、不解锁）；②**最小化到托盘时把已打开的独立窗口一并隐藏**（快照式：AI 助手 / 会话管理 / 拓扑 / 设置等此刻可见的顶层窗口全部 `Hide`，恢复时按快照逐个再显示、已关闭的跳过；**托盘期间新弹出的窗口（如审批弹窗）不进快照、照常显示**）；③关闭确认弹窗**去掉「取消」按钮**（只剩「最小化到托盘 / 退出程序」）；④**「退出程序」改为整个应用一起退出**：主窗口关闭即 `Application.Shutdown`（`ShutdownMode.OnMainWindowClose`），连同 AI 运维助手、审计日志、资产拓扑、SSH 会话管理、远程桌面、文件/文件夹同步、触发事件、端口转发、会话日志、设置等**所有独立窗口**一起关闭，不再残留；SSH 会话管理 / 远程桌面窗口「还有活动会话，确认关闭？」的二次确认在应用级退出时自动跳过（`AppShutdown.IsShuttingDown` 标记），分离窗口也不再触发「收回主窗口」。
- **应用锁（启动密码门卫）**：防止他人拿到电脑后直接打开本程序操作。**首次启动（或未设密码时每次启动）弹出「设置密码」窗口**（可「暂不启用」跳过，下次启动仍会提示）；已设置密码则**解锁后才创建主窗口**（未解锁不实例化任何会话/凭据 UI，直接关闭锁窗口=退出）。密码经 **PBKDF2-SHA256（210k 迭代）** 校验，哈希与盐经 DPAPI 加密落盘（明文密码绝不落盘），失败递进延迟（1s 起封顶 5s）；**Windows Hello**（指纹/PIN，`UserConsentVerifier`）可用时提供快捷解锁（未配置 Hello 的机器自动隐藏、回退密码）。**跟随 Windows 锁屏**：Win+L 离开后系统解锁回来时弹出模态重锁窗口（不可点 X 绕过，只能解锁或显式退出）；设置/解锁结果写入审计日志（`audit.db`，`app_lock_set` / `app_lock_unlock`，Gate 类）。设置入口：**设置 → 安全设置 → 应用锁**（设置/修改密码需验证旧密码，关闭需验证当前密码）。定位是**防随手操作的门卫，不是凭据金库**（凭据仍由 DPAPI(CurrentUser) 保护；忘记密码可删除 `config.json` 中 `appLock` 节点重置）。配置项 `appLock`；App 项目 TFM 升级为 `net8.0-windows10.0.19041.0`（WinRT API 编译面，运行最低版本不变）。
- **SSH 会话窗口 · 底部命令输入框增强（多行 + AI 分析）**：交互式终端底部的独立命令输入框改为**多行**（`AcceptsReturn`，`Enter` 发送 / `Shift+Enter` 换行；多行内容整段下发、统一换行为 `\r`，远端 shell 按行顺序执行——与粘贴脚本一致，保留跨行引号等结构），输入框左侧新增**「AI 分析」按钮**（与 MySQL 工作台「更多 → AI 分析此 SQL」同一模式）：把输入框内容以 ` ```bash ` 代码块预填进**临时聊天**窗口并自动切到**「运维专家」智能体**（分析要求由智能体系统提示承担，提示词只含代码块），用户可修改后再发送或直接发送；窗口已打开时复用并 `Activate`（再切智能体、再预填）。分离的独立会话窗口共用同一 DataTemplate，自动获得该能力。
- **AI 助手 · MCP 服务器管理（外部 MCP 接入）**：「AI 助手设置」新增**「MCP 服务器」标签页**，列表**内置 LitSSH MCP 置顶**（`Builtin=true`，**只能启停、传输配置不可改**；停用弹二次确认——停用后 AI 没有本机服务器/数据源等运维工具，且**跳过 exe 解析**，缺 exe 不再阻断助手启动）+ 可增删的**外部 MCP**。外部条目支持两种传输：**stdio**（`command` + `args` 每行一个 + `env` 每行 `KEY=VALUE`）与 **http**（SSE / Streamable HTTP 自动探测，`url` + `headers` 每行 `Name: Value`），**env / header 的值按当前 Windows 用户 DPAPI 加密**落盘（`ConfigService` 按分隔符取值加解密，旧配置明文自动迁移）。**启用前必须「校验连接」通过**：`McpServerValidator.ValidateAsync` 完成一次 `initialize + tools/list`（**15 秒超时**），成功记录 `lastValidatedAt` / `lastToolCount`，失败给出可读错误并**保持停用**（**校验失败的配置仍可保存**，即"停用态"）。运行期 `AgentRuntime.StartAsync` 变为**多宿主**：内置在前、外部在后逐个连接，**单个外部失败只降级为 `McpWarnings`（不阻断会话）**，状态栏提示"N 个外部 MCP 未连接"，系统提示列出该服务器的**失败原因**；全部宿主由 `AgentRuntime.Hosts` 持有、Dispose 时一并关停。**工具名冲突**：内置工具原名保留，外部工具**仅在与既有工具重名时**加 `<serverId>_` 前缀（再冲突加序号），映射（`` `fs_ssh_exec` → ssh_exec ``）写入系统提示；**能力边界段只统计内置工具**，外部工具不计入 `availableGroups`。系统提示新增 `## 外部 MCP 服务器` 段（每服务器工具数 / 失败原因 / 重名映射 / **第三方工具不经过本程序审批·过滤·审计的安全边界**，只读模式仅按其自报 `annotations.readOnlyHint` 过滤）+ 内置停用提示。保存并关闭设置窗口即**重建连接**（`ReloadProvidersAsync → EnsureRuntimeAsync` 本来就重建运行时）。配置项：`agent.mcpServers`（旧配置缺该字段由 `AgentMcpServerConfig.Normalize` 回填内置条目并置顶），`config.example.json` 补示例。**会话窗口快捷入口**：输入区「计划/执行模式」旁新增**「连接器」按钮**——弹出清单（内置置顶，显示 `已连接 · N 个工具` / `未连接: 原因` / `已停用`，状态来自运行时的 `AgentRuntime.McpHostStatuses` 快照），**勾选即启用/停用并立即保存 + 重建连接**（等效设置页「保存并关闭」；外部启用仍须校验通过、内置停用弹二次确认；任务进行中拒绝切换），末项直达「AI 助手设置 → MCP 服务器」。
- **AI 助手 · 技能根目录稳定化 + 绝对路径注入**：技能根目录默认改为 **`%APPDATA%\LitSSH\skills`**（**稳定目录**：跨重编译 / 升级保留；首次使用把随包 `skills/` **一次性复制**过去，用户已装技能不再随 bin 目录清理丢失；「AI 助手设置 → 技能根目录」可覆盖，设置提示文案同步更新）。`SkillRegistry.ResolveDir` 解析顺序 = 显式配置 → 稳定目录（缺失则由随包目录迁移）→ 随包 `skills/`，并新增 `stableDir` 参数供测试隔离（`Load` 同步透传，避免测试污染真实用户目录）。**该绝对路径注入系统提示的 `## 技能目录` 段**（含"每子目录一个技能 / 安装即复制到该根目录 / 本会话技能索引为启动时快照，新技能**下次会话**生效"的指引），`skill_list` / `skill_read` 的工具描述也带上根路径——**根治"模型找不到技能目录、把技能装到别处"的问题**。
- **AI 助手智能体（Agent）体系**：「AI 助手设置」新增**「智能体」标签页**，内置 3 个预置智能体（**通用聊天 chat / 运维专家 ops / 数据库分析 db**，均可编辑/删除，可自定义新增）；每个智能体 = 名称 + 描述 + **系统提示附加段** + 可选**回答风格覆盖**（concise/standard/detailed，空=跟随全局 `agent.responseStyle`，设置在提示词尾部「当前智能体」段**覆盖**全局篇幅规定——方案 A：全局风格段保持原位不动，切换智能体只失效尾部前缀缓存，其前的稳定前缀持续命中）。主窗口输入区（模式下拉与模型下拉之间）与**临时聊天**头部均提供**智能体下拉**（末项「智能体设置…」直达设置对应 Tab）；切换**就地热更新系统提示**（`AgentRuntime.SetActiveAgent` → `AgentSession.SetSystemPrompt`，不重连 MCP、不丢历史、**不改工具集**），忙时拦截提示。**按会话记忆**：`agent_sessions` 新增 `AgentId` 列（兼容迁移），打开会话恢复其智能体、切换时持久化并更新全局 `agent.activeAgentId`（新会话默认）；智能体被删除时回退 全局默认 → 第一个 → 无智能体（基础提示）。**临时聊天**智能体选择不持久化（每次默认 chat，可由调用方指定初始智能体）。**MySQL/PG 工作台「AI 分析此 SQL」改为只预填 ` ```sql ` 代码块**并自动切到「数据库分析」智能体（分析要求由智能体系统提示承担，不再重复塞入提示词）。设置窗口 Tab 定位改为**按 Header 文本查找**（杜绝插入新 Tab 后索引错位）。
- **AI 助手系统提示安全护栏**：安全段新增「**不要擅自安装/升级/卸载软件包、新增常驻服务或修改系统级配置（crontab/systemd/sshd/防火墙等）；确需时先说明理由与影响，征得用户同意后再执行**」。

- **远程桌面 · 交互细化**：管理页移除工具栏「连接 / 用外部客户端打开」按钮（改由列表右键 / 双击操作），列表**按「分组」归组显示**；内嵌「远程桌面」窗口改为**自绘顶部栏**（标题 + 新建连接 / 用外部客户端打开图标 + 最小化/最大化/关闭），**标签栏对齐「SSH 会话管理」样式**（文字 + 选中下划线，不再用蓝/灰填充块），**关闭标签需二次确认**（关闭窗口有会话时也确认）；标签支持**拖出分离为独立窗口 / 收回**（分离窗口自带「收回」按钮，标签右键亦可分离；重新挂载时仅重建查看器控件、**保留隧道**）；连接编辑的 RDP 分辨率改为**下拉预设**（自适应 / 1280×720 / 1600×900 / 1920×1080 / 2560×1440 / 3840×2160 / **自定义…**）。此外修正内嵌 RDP 关键点：连接前须设 `RDPPort`（默认 3389 会连错端口）、密码须经非脚本接口 `IMsTscNonScriptable.ClearTextPassword` 写入（ActiveX 不认 Windows 凭据管理器）、分辨率按**物理像素**（WPF DIP × DPI 缩放）设置，`Connected` 返回 VARIANT_BOOL 需安全转换；会话分辨率固定、之后仅以 `SmartSizing` 等比缩放（不重协商）。

- **远程桌面（RDP / VNC，独立功能）**：左侧导航新增**「远程桌面」**入口（主页数据看板亦有卡片），可**添加 / 管理 / 连接**独立的远程桌面连接（与 SSH / 数据源无关）。连接定义支持：协议（**RDP** / **VNC**，切换时自动带出默认端口 3389 / 5900）、主机 / 端口、账号 / **密码（本机 DPAPI 加密，永不返回给 AI）** / 域、**经某台已配置 SSH 服务器建立隧道**（复用现有端口转发服务，含 ProxyJump / 主机键策略 / 断线重连——可访问**只在堡垒机后可达的内网桌面**）、全屏、自动填充凭据、VNC 查看器路径（留空自动探测 TigerVNC / TightVNC / RealVNC / UltraVNC）、分组 / 标签 / 描述、最近连接时间。连接时：RDP 生成 `.rdp` 并调用系统 `mstsc`（需要自动登录时经 Windows 凭据管理器 `cmdkey` 暂存凭据，不写入 `.rdp` 明文文件）；VNC 调用探测到 / 指定的查看器；**查看器退出后自动拆除隧道**；每次连接**写入审计**（`audit.db`，Tool=`remote_desktop`）。配置持久化到 `config.json` 的 `remoteDesktops`（`schemaVersion` 3→4，`ConfigMigrator` 兼容旧配置）。**外部客户端**模式保留（管理页「用外部客户端打开」）。
- **远程桌面 · 内嵌视图（P2）**：管理页「连接」与双击改为打开**内嵌多标签窗口**（`RemoteDesktopSessionWindow`），标签内容用常驻控件切换可见性（避免切标签时 ActiveX / WebView2 被销毁重连）。
  - **RDP**：用系统自带 **RDP ActiveX 控件**（`mstscax.dll`，经 `System.Windows.Forms.AxHost` 运行时按 CLSID 承载，置于 `WindowsFormsHost`）在窗口内渲染远程桌面；账号/域写入控件，密码经 Windows 凭据管理器（`cmdkey`）暂存供控件取用。
  - **VNC**：用 **WebView2** 承载随包分发的 **noVNC** 页面（`Assets/novnc/`，MPL-2.0；页由 `VncWebHost` 在本机回环 `http://127.0.0.1:<port>` 提供，WS 桥 `/websockify` 把 WebSocket 二进制帧桥接到目标 VNC 的 TCP——即经 SSH 隧道后的本地端口；用 http 而非 https 以避免安全上下文对明文 `ws://` 的混合内容拦截）。缺 WebView2 运行时会给出安装提示。
  - 每次连接仍**写入审计**；关闭标签/窗口**自动拆除隧道**。RDP 要求系统 RDP 组件（Windows 自带），VNC 需 WebView2 运行时（常随 Edge/Windows 预装）。


- **SSH 服务器右键「打开代理浏览器 (SOCKS)」**：一键建立经该服务器的**动态 SOCKS5 转发**（复用现有端口转发服务，含 ProxyJump / 主机键策略 / 断线重连），并用**独立临时 profile** 启动 Chromium 系浏览器（Edge/Chrome/Brave/Chromium，**自动检测**），以 `--proxy-server=socks5://127.0.0.1:<port>` 让浏览器**全部 HTTP/HTTPS 流量走该服务器出口**；额外 `--disable-quic`、`--force-webrtc-ip-handling-policy=disable_non_proxied_udp` 降低泄漏，并打开**校验页**显示出口 IP（页内用 `fetch` 多源回退获取，且**新标签页直接打开** `ipinfo.io/json` ——原 iframe 会被浏览器的跟踪防护拦截）。关闭浏览器即**自动停止转发并清理临时 profile**。端口转发管理里对**已有「动态 SOCKS」**也提供「**开浏览器**」：未运行时自动启动转发，关闭浏览器后若为本次自动启动则一并停止。**限制**：仅覆盖 HTTP/HTTPS（QUIC/WebRTC 已尽量禁用，但仍可能有绕过，如插件/原生 socket；强隔离请配合防火墙封 UDP 或 Windows Sandbox/虚拟机）；要求 SSH 服务器 `AllowTcpForwarding`（默认允许）且账号能从**服务器所在网络出网**；需本机装有 Chromium 系浏览器。
- **数据库工作台（第 1 批：执行体验）**：`Ctrl+Enter` **执行选区 / 光标所在语句**，`Ctrl+Shift+Enter` 执行全部（多语句**逐条执行**、只读逐条校验）；查询**可取消**（Esc / 取消按钮），超时改用该数据源的 `TimeoutSeconds`（不再固定 60s）。新增 `SqlScriptSplitter`：正确识别单引号字符串（`''` 转义）、双引号/反引号标识符、`--`/`#` 行注释、`/* */`（支持嵌套）块注释、PostgreSQL `$tag$...$tag$` 美元引用后再按顶层 `;` 切分。查询编辑器由纯 `TextBox` 换成 **AvalonEdit**（SQL 语法高亮 + 行号；与 VM 双向同步）。查询区支持**多标签**（`+ 新建查询` / 关闭 / 切换，每个标签独立的 SQL、结果、分页与写模式）。结果区新增**执行计划**按钮（调用驱动 `ExplainAsync`）、**导出 JSON**、右键**复制为 INSERT 语句**、**NULL 灰字显示**（`NULL`，仅显示不影响复制/导出）、双击列头**列宽自适应**。数据源编辑新增**启用连接池**复选框（仅直连生效，经 SSH 隧道时自动禁用），且驱动命令超时改用该数据源的 `TimeoutSeconds`；数据源编辑窗口加宽。**对象浏览器**支持切换**表 / 视图 / 存储过程**，选中表显示**索引**，右键可**生成 SELECT / INSERT / COUNT / CALL** 填入编辑器。新增 `QueryMultiAsync`（`ISchemaObjectExplorer` 浏览视图/过程/索引），支持**多结果集**（存储过程 / 多语句）：结果区出现 **结果 1 / 结果 2 …** 子标签。修复切换**表/视图/存储过程**时**列结构未清空**；「预览选中表」移入对象列表**右键菜单**（移除工具栏按钮）；**分页**改为“表预览”专属、按**标签**记录页码/总行数（普通查询不显示分页；切换标签各自独立，不再串台）；SQL 编辑器新增 **schema 感知自动补全**（关键字 + 当前表/列名，AvalonEdit `CompletionWindow`）；**支持多工作台窗口**（同一数据源复用窗口，不同数据源并行打开）。SQL 编辑器支持 **Ctrl+F 查找/替换**（AvalonEdit `SearchPanel`）、**当前行高亮**与自动缩进；对象浏览器新增**搜索过滤 + 刷新**；结果网格新增**列筛选行、行号、冻结首列**。结果区新增**消息/错误面板**（记录失败/警告，可清空）；编辑器支持**打开/保存 .sql**；对象右键新增**生成 CREATE TABLE (DDL)**；EXPLAIN 的单列文本计划（PostgreSQL）以**等宽文本面板可视化**（可关闭）；补充数据源驱动契约/对象模型单测。SQL 输入区支持**拖动分隔条调整高度**（执行/导出等工具行移到**结果区顶部**，编辑区与结果区共享空间、各有最小高度）；顶部工具栏改为**卡片**样式并加图标，列/索引列表去掉横向滚动、更紧凑；结果区新增**空状态提示**。工具栏「导出 / 更多」合并为**下拉菜单**（导出 CSV/JSON、执行计划/刷新对象）；窗口标题显示 **数据源 · 库**；底部状态栏显示当前连接信息；编辑器代码区背景**跟随主题**。结果区改为 **SSMS 风格统一子标签**（结果 1/2… / 消息 / 计划 并列切换，不再“计划覆盖网格 + 消息在底部”）。左栏对象区改为**单棵树**（表 / 视图 / 存储过程 → 列 / 索引，展开**懒加载**、顶部搜索与刷新、右键生成 SQL/预览、双击预览表/视图）。修复对象树**空白处右键误弹对象菜单**（仅命中表/视图/存储过程节点才弹）；默认**展开「表」**、搜索时**保持展开/折叠状态**。新增 **SQL 格式化**（编辑器「格式化」按钮，子句换行/关键字大写）与**查询历史**（本机持久化，「历史」下拉可回填/清空）。历史下拉在**选择一条或点击面板外**时自动关闭；底部栏移除「关闭」按钮与重复的库信息（标题栏已含），仅保留状态消息。对象目录查询**按段容错**（表/视图/存储过程任一段失败不影响其它并给出提示）；**任意 SELECT 可翻页**（无 LIMIT/OFFSET 的 SELECT 自动分页，`上一页/下一页/第 x 页`）；**删除/归档 AI 会话时自动关闭其本机终端**（避免孤儿终端）；**EXPLAIN 升级为计划树**（PostgreSQL `EXPLAIN (FORMAT JSON)` 解析为 树 + 成本/行数，附 `SqlPlanParser` 单测）。对象树展示**表/列注释**与**外键**（新增 `ISchemaObjectExplorer.ListForeignKeysAsync`，表节点下含「外键」分组）；「导出」新增 **Excel (.xlsx)**（自研 `XlsxWriter`，无第三方依赖、数值为数字单元格，附单测）；「更多 ▾」新增 **AI 分析此 SQL**——打开**独立的临时聊天**（与 AI 运维助手的临时聊天互不影响）并预填 SQL + 性能/风险/影响/优化提示词，可编辑或直接发送。
- **AI 助手用量 / 成本面板**：`+ 菜单 → 用量 / 成本…` 展示**当前会话**的请求次数、输入/输出/缓存命中 token 与**估算成本**；在「AI 助手设置 → 大模型设置」为每个模型填「入/出价格（每百万 token）」后显示成本。
- **批量执行增强（快速命令 / 一键巡检）**：批量执行窗口新增一排**快速命令按钮**（一键巡检、磁盘/inode/内存/端口/CPU·内存 Top/大目录/日志/容器/网络）；**写操作以 `⚠` 标注、执行前弹确认**（"带审批"）；沿用多机并行执行。
- **快照趋势图**：服务器快照历史窗口新增「趋势图」——用该服务器最近 50 份成功快照的 `resource` 维度，画**内存% / 磁盘%(最大分区) / Swap% / 负载%(1m/核)** 趋势折线（自绘轻量图表，纵轴 0-100%，无需第三方图表库）。
- **SSH 会话录像 / 回放**：终端会话记录升级为 **asciicast v2（`.cast`）**——带时间戳的输入/输出事件；「会话日志」窗口对 `.cast` 提供**播放 / 暂停 / 重开 / 倍速回放**（去 ANSI 文本渲染），并保留原 `.log` 纯文本。开关 `设置 → 终端 → 记录会话日志`（默认开）。
- **触发事件总览（编排可视化）**：「触发事件」窗口新增 **「总览」**——只读展示每个触发事件的启用/目标/**同步触发**、**调度描述 + 下次运行时间 + 上次结果**，以及**动作流水线可视化**（步骤卡片 `→` 串联，标注"失败中断/失败继续"）。
- **凭据增强 / ProxyJump**：
  - **密钥生成**：服务器编辑支持调用系统 `ssh-keygen` 生成 **ed25519 / rsa(4096)**（缺失时回退**内置 RSA**），自动填入密钥路径并展示 **OpenSSH 公钥**（一键复制到目标机 `~/.ssh/authorized_keys`）。
  - **凭据轮换提醒**：`credentialUpdatedAt`（凭据变更自动刷新）+ 每服务器 `credentialRotateDays`（0=关），超期在编辑窗口提示。
  - **跳板机（ProxyJump）**：服务器可指定另一台已配置服务器作为跳板——连接时**先连跳板→建本地端口转发→经 `127.0.0.1` 连目标**；目标连接用 `SshClient/SftpClient` 的**子类**承载"跳板隧道"，Dispose 时一并释放（生命周期自动绑定）。**统一在 SSH 连接工厂 `SshClientFactory` 生效**，故**终端(含文件管理)/SFTP/命令/同步(SFTP 路径)/触发事件/拓扑发现/MCP 工具(含连接池复用)/数据源隧道/端口转发**均自动支持；主机密钥（TOFU）仍按**真实目标**校验；连接池键已加入跳板标识。仅支持**单级**跳板；**不能选自身、也不能选与本机同 Host 的服务器**（保存时按最终 Host 复核；连接工厂亦有兜底校验，防止直接改 `config.json` 绕过）。候选下拉已过滤同 Host/已挂跳板的服务器。
  - **CLI / rsync 也支持跳板**：`litssh connect/run` 改为复用连接工厂（自动支持跳板）；rsync/tar 的"源服务器→目标"直连路径会在**源机**执行的 `ssh -e` 上加 `-J <跳板>`（**前提：源机自身能连跳板机并完成认证**，否则该项按 native rsync 行为失败/报错）。
- **本机 PowerShell 终端 + AI 本地 CLI**：新增 **ConPTY 真实本机终端**（复用现有终端渲染/录制），从「新建连接 → 本机 PowerShell 终端」打开；内置助手新增 **`local_shell` 工具**，在同一**用户可见**会话里执行 CLI（共享 cwd/env，命令对用户可见）。安全（**逻辑基线**）：**硬白名单**（仅白名单内 CLI，如 git/dotnet/npm/gh/aws/az/terraform/kubectl 等，其余直接拒绝）、危险命令拦截、**每次人工审批**（对话框支持「总是允许此命令」并记忆）、超时中断 + 进程树清理、`-NoProfile`。默认**关闭**，在「AI 助手设置 → 本机终端」启用并配置白名单/超时/输出上限。宿主扩展点：`AgentRuntime.StartAsync(extraTools:)` + 公开 `IAgentTool`。**终端按 AI 会话隔离**：每个会话记住/复用自己那一个本机终端（校验进程存活，标签被关/进程失效则自动重建一个新的），互不影响；AI 打开的终端标题带 `[AI-进程Id]` 以便区分，手动亦可打开多个本机终端。工具说明明确「**同一会话单终端、串行执行**」（并**忙时直接拒绝**而非排队），且对疑似**交互式/长驻命令**（vim/top/tail -f/watch/无参 REPL、`docker exec -it` 等）**前置拒绝**并提示非交互写法。**健壮性与体验**：命令输出改为 **Tee 实时显示**（同时落盘供模型读取，自动去除 ANSI），结果附带 **退出码 + 耗时 + 当前工作目录**；超时先 Ctrl+C 等其回到提示符，宽限期内未回则**强杀整棵进程树**（shell 放入 Job Object，关闭时整树回收）；「总是允许」支持**前缀匹配**并可在设置里**逐条移除**。
- **AI 助手「任务/会话」管理**：点「新建任务」不再立即写入会话记录——进入**未保存的新任务**态，**首次发送消息时才落库**并在侧栏出现（空置的新任务不产生记录；启动时无会话也进入新任务态；删除/归档最后一会话不再自动建空会话）。侧栏新增**分组**：`agent_sessions` 增 `Group` 列 + `agent_groups` 表（**空分组也显示**），会话可右键**移动到分组**、分组可**重命名 / 删除**（删除时其会话归入「未分组」）；「未分组」置顶、其余按名称排序；**归档/恢复保留分组**。侧栏标题改为「任务/会话」，并在「新建任务」下新增「**新建分组**」按钮。侧栏支持**拖拽**与分组**折叠/展开**：会话行可拖到分组标题（入组并置顶），也可拖到**其他会话行**（松手后移动到该会话所在分组并排到它之后）；分组标题可互相拖拽**排序**（「未分组」固定置顶、不可拖动/改名/删除）；分组可点击**折叠/展开**（状态记入本机偏好）。排序**持久化**（`agent_sessions.SortOrder` / `agent_groups.SortOrder`，新任务默认排最前）。并修正**右键行为**：右键只对鼠标所在行生效——会话行弹会话菜单（**菜单打开期间该行保持悬浮高亮**，且严格作用于该行）、分组标题弹分组菜单（全行可触发）、空白处不弹菜单（键盘上下文键则作用于当前会话）。
- **MCP Resources（只读上下文）**：向任意 MCP 客户端暴露**只读资源**，供“浏览 / 挂载”为上下文（无需调用工具）：`litssh://servers`（服务器清单）、`litssh://datasources`（数据源清单）、`litssh://usage-guide`（使用指南，等价 `mcp_usage_guide`）、`litssh://topology`（资产拓扑）、`litssh://server/{id}/snapshot/latest`（**资源模板**：某服务器最新快照概览，含 resource 维度 + warnings/riskLevel）。**不含密码/密钥**、禁用服务器已排除、**按启用工具分组注册**（与工具/说明一致）。顺带把 `mcp_usage_guide` 与指南资源的 JSON 改为**不转义中文**（省 token、易读）。

- **触发事件（本机 / 远程）**：新增「触发事件」窗口（主页看板卡片 / SSH 服务器管理页按钮），对**本机或远程服务器**按顺序执行一条**动作流水线**：
  - **Git 提交推送**：`status`→`add -A`→`commit`→`push`（无变更自动跳过；本机用进程执行器、远程用 `ISshService`；提交信息支持变量模板）。
  - **Webhook 通知**：通用 JSON / 钉钉 / 企业微信 / 飞书；`{{变量}}` 模板渲染，钉钉/飞书 HMAC 加签；密钥 DPAPI 加密；自定义请求头与超时。
  - **目录备份**：本机 `Zip`（`System.IO.Compression`）/ 远程 `tar -czf`（缺失回退 `zip`）；输出 `触发事件名-时间戳.扩展名`，可「保留最近 N 份」自动清理最旧。
  - **数据库备份**：MySQL / PostgreSQL，**整库或指定表**（表名逗号/换行分隔，PostgreSQL 用 `schema.table`）；产物 SQL 转储、可选 gzip 为 `.sql.gz`，输出 `触发事件名-数据源-库-时间戳.sql[.gz]`，可「保留最近 N 份」。**输出目录随执行目标**：目标=本机时在本机执行并落盘（优先本机 `mysqldump` / `pg_dump`，可指定工具路径；经 SSH 隧道的数据源自动建隧道；探测不到工具时退化为**托管流式导出**：`SHOW CREATE TABLE` / `information_schema` 生成 DDL + 边读边写 INSERT，全程流式落盘）；目标=远程服务器时**在服务器上执行并落盘到服务器目录**（需服务器安装客户端工具且能访问该数据库）。密码仅经 `MYSQL_PWD` / `PGPASSWORD` 环境变量传入、不写日志。
  - **触发源**可叠加：手动「立即执行」、定时（固定间隔 / 每周 / 每月，复用 `SyncSchedule.ComputeNextRun` 参数化重载）、**被同步任务「事件触发」**：触发事件侧勾选「允许被同步任务事件触发」（`AllowEventTrigger`），同步任务末尾配置 `eventHooks`（`SyncEventHook{triggerId, onlyOnSuccess}`）选择要触发的触发事件及条件（**无条件触发** / **仅同步成功时触发**）；`SyncScheduler.TaskCompleted` 事件驱动。
  - 单步失败默认中断后续，可勾选「失败后继续」（如失败也要发通知）；失败按指数退避重试（≤5 次）；git、目录备份与数据库备份写入审计库；执行日志 `logs/trigger-*.log`、状态 `trigger-state.json`；`App/Services/TriggerScheduler` 仅 App 运行期生效。配置 `triggers`（`ConfigMigrator` 兼容旧配置）。
- **数据库工作台**：数据源管理页顶部「打开工作台」或右键「打开工作台」（MySQL / PostgreSQL / Redis 可见，打开即定位到该数据源）打开独立窗口。
  - **SQL 模式（MySQL / PostgreSQL）**：对象浏览（库 → 表 → 列，含主键/可空/类型与估算行数）+ SQL 查询 + 表数据分页预览（`LIMIT/OFFSET` + `COUNT(*)`）+ **结果复制**（右键复制单元格 / 整行 / 全部结果、双击查看完整内容）+ **导出 CSV**。
  - **Redis 模式**：左侧 **键浏览**（`SCAN MATCH` 模式扫描、双击键按类型读取值：GET/HGETALL/LRANGE/SMEMBERS/ZRANGE/XRANGE，并显示**键类型**与**生存时间**（`TTL`：剩余秒数 / 永不过期 / 键不存在）），右侧命令输入框（只读模式仅白名单读命令；写入模式经确认后执行，`RedisCommandPolicy` 危险命令硬拒绝）；对象模型差异不适用表/列分页预览。
  - 新增可选能力接口 `IDatabaseExplorer`（`MySqlDriver` / `PostgresDriver` 实现库/表/列浏览）与 `IRedisKeyExplorer`（`RedisDriver` 实现 SCAN + 按类型取值），均不影响 `IDatasourceDriver`。
  - **只读 / 写入模式**：顶部开关，**默认只读**；写入模式下窗口标题、SQL 输入框（红框+浅红底）、执行按钮（红色「执行写入」）与结果区「写入模式」徽标均强区分，避免混淆。
  - **SQL 片段**：按「数据源 · 库」分组收藏 SQL（SQL 标题行右侧 `★ 收藏当前 SQL`，去重）；顶部「SQL片段 (N)」按钮弹出**菜单样式下拉**（标题显示当前数据源·库），每行点击**追加**到 SQL 输入框（读写模式通用）、右侧 `✕` 删除（二次确认），底部亦有「收藏当前 SQL」；落盘 `%APPDATA%\LitSSH\db-sql-snippets.json`。
  - 写操作**复用现有安全链**：`SqlFilterService.CheckWrite` 硬拦截（无 WHERE 的 UPDATE/DELETE、DROP/TRUNCATE/GRANT 等、只读语句、多语句）→ **执行前预览确认对话框**（展示将执行的 SQL + 尽力解析 UPDATE/DELETE 的 WHERE 得到的**预计影响行数**与**受影响样本行**，最多 100 行；`WriteApproval=AutoApprove` 时跳过确认）→ 执行后**成功/失败一律弹窗提示**（影响行数/耗时或错误）→ 写入 `audit.db`（Operation=Execute，Tool=app_workbench）；**只读数据源禁止开启写入**。驱动器 `ExecuteAsync` 执行。
- **数据库备份并发闸**：新增 `security.limits.maxConcurrentBackups`（默认 2，安全设置中可调 1~16），全局限制同时运行的数据库备份数，避免多任务同时 dump 压垮本机 / 目标。
- **端口转发**：删除规则增加二次确认弹窗。
- **AI 助手上下文 / 缓存 / 图片专项**（设计文档见 `docs/AGENT_HARNESS_REVIEW.md`）：
  - **发送前孤儿工具调用修复**：`AgentSession.RepairOrphanToolCalls` 在构造、`ReplaceHistory` 与每轮发送前，为缺失结果的 `tool_calls` 补合成结果，与轮内 catch 构成双层防护，避免严格端点因配对缺失报 400。
  - **缓存命中观测**：读取 provider 返回的缓存命中输入 token（`UsageDetails.CachedInputTokenCount`），顶部上下文信息增加「缓存命中 X (Z%)」。
  - **图片发送前压缩**：`AttachmentService.DownscaleImage` 按最长边 ≤1536px 等比缩放（JPEG→JPEG q85、其余→PNG，仅在确实变小或超尺寸时重编码）。
  - **图像 token 估算**：`TokenEstimator.EstimateData` 解析 PNG/JPEG/GIF/BMP 尺寸并按分块公式估算（此前图片按 0 计，导致上下文占用系统性偏低）。
  - **结构化滚动摘要**：`SummarizeAsync` 摘要改为固定字段（目标/当前状态/涉及主机与端口/已执行操作/错误/待办/必须保留）。
- **AI 助手重启恢复与技能加载**（设计文档见 `docs/AGENT_HARNESS_REVIEW.md`）：
  - **工具轨迹落库**：`agent_messages` 新增 `role="tool"` 行（tool/args/result/ok/ms），重开程序后 UI 还原可展开工具步骤、模型获得该轮文本工具轨迹（不再丢失取证依据）。
  - **统一历史重建**：`AgentViewModel.BuildTranscriptAsync` 作为会话记录的**唯一解析入口**（加载/切模型/编辑重发共用），消除此前"剥离标记 vs 原始文本"两条路径不一致的问题。
  - **技能两段式加载**：`SkillRegistry` 对大技能**内联核心章节**（preamble + 前置 `##` 节，累计 ≤4000 字符），其余章节列标题 + `skill_read` 提示——核心流程开箱即用、又不常驻 16KB 全文。
- **AI 助手执行效率与恢复（第二批）**：
  - **并行只读工具**：同一轮多个 `tool_calls` 时只读调用并行执行（写/审批串行），结果按原始顺序回填并用 `CallId` 精确映射 UI 步骤。
  - **连续失败熔断**：同一轮连续 4 次工具失败（含审批被拒）即停止，避免空转。
  - **类型感知截断**：日志类工具（`log_tail`/`*_logs`）超限保留**尾部最新内容**，其余保留头部。
  - **图片重载配额**：重启恢复时视觉模型最多重建最近 3 张历史图片，其余保留文本标记。
  - **任务计划持久化**：`agent_sessions.Plan` 跨重启恢复任务计划面板。
  - **上下文预算分解**：上下文圆环悬浮显示构成（系统提示/工具定义/摘要/用户/助手/工具结果 + 请求合计），工具定义单独列出（固定前缀、不计入裁剪上限）。
  - **工作流规则强化**：系统提示固化"先只读取证→定位根因→最小变更→变更后验证（含影响与回滚）"与"同一调用连续失败两次即换策略"。
- **AI 助手思考过程展示**：识别推理模型（如 `kimi-k2-thinking` / R1 等）输出的 reasoning 增量并实时展示——助手回答卡片新增可折叠「思考过程」区（运行中展开、完成后自动折叠；历史恢复默认折叠，点开可看）。思考内容仅用于展示，**不回喂上下文**（部分端点不接受输入含 reasoning，且会白白撑大历史）。
- **AI 助手工具结果 JSON 规范化（省 token）**：MCP C# SDK 默认用「HTML 安全」编码器序列化，中文/引号会被写成 `\uXXXX` / `\u0022`（数据本身正确但费 token 且界面难读）。现新增 `ToolResultFormatter`：**喂给模型**用紧凑模式（非 ASCII 不转义、展开内嵌 JSON），**界面展示**用缩进格式；入参序列化同步改为非转义。工具结果在进入模型上下文前统一规范化。
- **AI 助手工具过程/思考过程展示优化**：展开工具调用或思考过程时按内容撑高，不再形成内部滚动条，会话区始终只保留单一滚动条。
- **MCP 能力说明随工具分组裁剪（避免调用不存在的工具）**：`mcp_usage_guide`、MCP `server instructions` 与 AI 助手系统提示现在都依据**实际启用的工具分组**生成——只列出/路由到真实暴露的工具，并明确告知被停用的能力域（提示"不要调用、如需请到桌面 App 开启"）。`mcp_usage_guide` 的工具清单与"意图→工具"路由行按启用分组过滤（引用了被停用分组工具的行自动裁剪）。工具↔分组映射统一收敛到 `ToolGroups.GroupOf`（Agent 与服务端共用一个实现）。
- **技能按能力域裁剪**：注入系统提示的技能会扫描其正文引用的工具域，对照当前实际可用域——技能涉及域**全部被停用则整体跳过**；**部分被停用则在技能顶部加提示**（点名被停用的域，提醒相关步骤不可执行并给替代方案）。配置全开时行为不变。
- **技能选择器与预置技能包**：
  - 技能根目录改为**目录式**（每子目录一个技能，含 `SKILL.md`，可带 `references/`；兼容顶层单文件 `*.md`）。App 内置 `skills/` 随包分发**一组预置运维技能**：nginx 5xx、磁盘/inode 满、CPU/内存飙高、MySQL 排障、Redis 排障、容器起不来、systemd 服务失败、TLS 证书到期、日志定位与错误聚合（另有总纲 `litssh-mcp-ops-skill`）。
  - **默认"索引模式"**：未选择技能时，系统提示只注入**技能索引**（名称 + 适用描述 + 域 + `skill_read` 提示），极省 token；激活后其正文才注入。
  - **选择入口**：输入框斜杠命令 **`/技能`** → 技能选择窗口（多选 + 域标签 + Markdown 预览 + 可用性提示），以及输入区 **`+` 菜单 → 选择技能…**；已激活技能以 **chip** 显示在输入框上方，可单个移除。
  - 激活为**会话级**并持久化到 `agent_sessions.ActiveSkills`（跨重启恢复）；切换技能**就地热更新**系统提示（`AgentSession.SetSystemPrompt`，不重连 MCP、不丢历史）。
  - 技能目录项按当前可用域标注（`SkillRegistry.List` + `SkillInfo`），与"技能按域裁剪"衔接。
- **提示词模板 / 快捷指令**：AI 助手设置新增「提示词模板」Tab，用户可自定义 `/命令名` → 提示词正文；输入框键入 `/` 即在命令面板出现，选中后把提示词**填入输入框**（补充目标服务器/数据源后回车发送）；内置 3 个示例（整机巡检 / 排查 nginx / 数据库体检）。落盘 `%APPDATA%\LitSSH\agent-prompts.json`，命令名去重校验。
- **技能体系增强**：
  - 预置技能再补 4 个：**端口占用与进程**、**网络连通性**、**发布后验证与回滚**、**Java GC/JVM 诊断**（共 13 个预置技能 + 总纲）。
  - **codex 风格内联展开**：输入框键入 `/` 时，命令列表中直接**内联列出技能**（`技能` 分组标题 + 缩进项），↑/↓ 选择、Enter/点击**即切换激活**（✓=已激活，面板保持打开可多选）；`/技能` 仍打开带预览的完整选择窗口。
  - 技能选择窗口新增 **导入文件… / 导入目录…**（写入 `<根目录>/<名>/SKILL.md`）、**打开技能目录**、**双击技能用外部编辑器打开**。
- **AI 助手加载体验**：打开窗口 / 切换会话时显示**加载遮罩动画**（进度环 + "正在加载…"文案），消除空白期；启动**裁剪（Prune）改为后台执行**、不阻塞首屏；切换会话时**先上屏对话内容再后台连接模型**（连接进度走状态点 / 状态栏）。会话侧栏只加载**元数据**、模型上下文只重建**当前会话**的消息（不加载全部会话内容）。
- **单会话转录分页懒加载**：超长会话默认只渲染最近 **20 轮**，顶部「加载更早的记录」按页向上展开——**只影响渲染**，模型上下文与持久化仍是该会话全量（避免超长会话一次性渲染卡顿）。
- **AI 助手冷启动上下文收敛**：新增 `agent.coldStartRecentTurns`（默认 30，0=不限制；「AI 助手设置 → 上下文与输出」可调）。冷启动 / 重载 / 编辑重发时**只向模型注入最近 N 轮**，更早的由**滚动摘要**代表，降低冷启动首次请求开销；**仅影响模型上下文**，界面仍可翻看 / 导出全部记录。
- **AI 助手计划模式（缓存友好）**：输入区（上下文环**左侧**）提供**模式下拉（执行模式 / 计划模式）**（会话级、持久化到 `agent_sessions.PlanMode`）。切换**不改系统提示、不改工具集**，只在每轮用户消息**尾部**附"计划模式"提示并在调用前**拦截写工具**——因此**不会使 prompt 前缀缓存失效**。计划模式下模型只做只读调研、可列计划、**不得擅自执行**；若用户指令含执行意图会明确提示"需切换到执行模式"，切回执行模式后由**用户自行发指令**（不自动发送）。
- **AI 助手工具级审批策略**：`+ 菜单 → 工具审批策略…` 打开逐工具设置（**默认 / 允许 / 拒绝**，会话级、持久化到 `agent_sessions.ToolPolicy`）。**拒绝**在调用前拦截（不执行、返回明确提示）；**允许**表示 Agent 不再额外限制。注意：Agent 层策略**不绕过 MCP 服务端的桌面审批**（敏感操作安全边界仍在服务端，全局策略见「安全设置 → 审批模式」）。

### 变更

- **AI 助手默认上下文 token 上限由 24000 提升至 96000**（适配现代模型普遍 ≥64K/1M 窗口）；旧配置若仍为 24000 会在加载时自动升级（`ConfigMigrator`），用户手动设过的其它值不受影响。
- **移除会话级 `/只读`、`/工具` 斜杠命令**（含工具分组对话框与「只读/工具」标签）：它们会重建 tools 数组，导致 prompt 缓存**整段失效**。只读/分组改由全局设置（`agent.readOnly`、`agent.allowedToolGroups`）在运行时启动时一次性生效（含本地写工具），会话期内工具集恒定 → 缓存前缀稳定；危险操作仍由桌面审批兜底。
- **设置 → 关于页内容更新**：简介、最近更新亮点（远程桌面、AI 智能体体系、本机 PowerShell 终端 + AI 本地 CLI、MCP Resources、数据库工作台执行体验）、桌面 App 核心功能（新增远程桌面 / 本机终端、AI 助手补智能体体系）与技术栈（补 AvalonEdit、RDP ActiveX / WebView2 + noVNC）。
- **主页快捷入口移除「远程桌面」**：仅移除主页「快捷入口」中的按钮；左侧导航栏图标与「数据看板」远程桌面统计卡片保留。
- **远程桌面列表「隧道」列改为简明标记**：不再显示隧道服务器 Id，启用隧道的连接显示 `√`、未启用为空（列宽 130 → 46）。

## [1.2.0] - 2026-10-07

### 新增

- **AI 运维助手（App 内置大模型运维智能体）**：新增 `LitSSHmcp.Agent` 类库与 App 菜单「AI 运维助手」。在 App 内配置 **OpenAI 兼容大模型**（DeepSeek/通义千问/Kimi/智谱GLM/硅基流动/本地 Ollama 等，**多模型启用/停用**、设默认），用自然语言对话完成服务器/数据源/应用运维。
  - 通过**内嵌 MCP 客户端**（stdio 子进程启动本机 `LitSSHmcp.McpServer`）驱动**全部 MCP 工具**，因此**桌面审批、命令过滤器、审计哈希链、工具分组开关**与第三方 AI 客户端完全一致（安全一致）。
  - 手动驱动"模型→工具调用→结果回灌"循环（`Microsoft.Extensions.AI` + 官方 `ModelContextProtocol` 客户端 + OpenAI 兼容连接器）；**流式输出**（逐段渲染）。
  - **上下文管理**：多会话持久化到独立库 `%APPDATA%\LitSSH\agent.db`（`agent_sessions`/`agent_messages`），支持**新建/切换/删除会话**、首条消息**自动命名**，按 `agent.contextLimit` 裁剪。
  - **多模型热切换**：对话中切换模型（保持当前会话上下文）；**工具分组裁剪**（`agent.allowedToolGroups`）。
  - **技能(Skill)**：默认把 `docs/litssh-mcp-ops-skill` 注入系统提示，可配置自定义技能目录；另拼入 MCP `server instructions` 与用户附加提示。**技能文档保持通用**（不写具体工具名），并新增**工作区文档工具** `ops_doc_read` / `ops_doc_write` / `ops_doc_list`（本地、非 MCP，路径限定在 `agent.workspaceDir` 内）——其能力由**系统提示自动注入**（而非写进 skill），使"维护 OPS_ASSETS.md 运维资产档案"真正可用。
  - **展示**：回答以 **Markdown 渲染**（标题/粗体/斜体/删除线/行内代码/围栏代码块[**语法高亮 + 复制按钮**]/**表格**/有序无序列表[**含嵌套与任务清单**]/引用/链接/**图片**/**脚注**），**流式去抖 150ms**；支持**整条回答复制**与**导出 Markdown**；每轮对话按 **用户指令(带时间) → 工具过程 → 回答(带时间与耗时)** 展示——工具过程挂在对应指令下方，**运行中展开、完成后自动折叠，点用户指令可再次展开**；助手窗口为**非模态独立窗口**，**最大化/拉宽时内容自适应**，可与主界面同时操作。
  - **只读模式**：仅向模型暴露只读工具；模型 API Key 纳入 DPAPI `enc:` 加密存储。
  - **交互增强**：**重新生成 / 编辑并重发 / 继续**；工具过程每条**可展开**查看完整入参/结果、显示**耗时**，失败可**重试**；会话支持**搜索 / 重命名 / 导出**（导出 Markdown，工具过程用 `<details>` 折叠）；顶部显示**上下文用量估算与轮数**，并提供**模型连通性自检**（发一次最小请求）。
  - **规划与进度**：内置 `update_plan` 工具（**单代理内规划器**）——多步任务先产出计划、执行中持续更新；顶部**任务计划面板**显示进度（☑/☐）。
  - **工具分组裁剪 UI**：设置里可勾选允许的工具分组（`agent.allowedToolGroups`）。
  - **审批联动**：破坏性工具调用时步骤显示"调用中…（可能等待人工审批）"；审批被拒/超时（`rejected`/`approval_timeout`）作为失败反馈展示并可重试。
  - **长期记忆 / RAG**：可配置 OpenAI 兼容 **Embeddings** 端点，把历史会话与**工作区文档**向量化存入本地库（`agent.db`），对话时按相似度**召回**并注入上下文（`agent.memory.{enabled,endpoint,model,apiKey,topK}`）。
  - **健壮性与安全**：每模型可配 **超时 / 重试 / 并发**（`ResilientChatClient`，对网络/超时/限流/5xx 瞬时错误自动退避重试；流式仅在首包前重试）；对话与"测试连接"均给出**错误分类**（`auth`/`rate_limit`/`timeout`/`network`/`server`/`bad_request`）；系统提示加入**提示注入防护**（工具输出视为不可信数据、破坏性操作须用户明确要求）；**会话保留策略**（最多会话数 / 每会话消息上限 / 保留天数，启动与每轮后自动裁剪）。真实端点 **E2E 冒烟测试**由环境变量门控（`LITSSH_AGENT_E2E=1` + `LITSSH_AGENT_E2E_KEY`）。
  - **上下文管理（按轮裁剪 + 滚动摘要）**：改为按**轮边界**（`user` 消息）从最旧开始丢弃**整轮**，天然保持 `assistant(tool_calls) ↔ tool` **配对完整**（避免孤儿工具消息导致严格端点报 400）；同时受**双阈值**约束——条数 `agent.contextLimit` 与 token 估算 `agent.contextTokenLimit`；被裁掉的旧轮在 `agent.autoSummarize=true` 时由模型压缩为**滚动摘要**注入，摘要失败则直接丢弃旧轮。此外：**单个工具结果注入上限** `agent.toolResultMaxChars`（默认 4000 字符，超出截断、完整结果仍见界面）、**提前触发比例** `agent.contextTrimRatio`（默认 0.8，达到上限 80% 即开始裁剪）、以及**轮内复查**——每步工具调用后重新按阈值裁剪，若单轮工具输出仍超限则把本轮较早的工具结果内容替换为占位符（保持 `CallId` 配对，提示模型必要时重调）。
  - **Token 优化（技能按需 + 快照分节）**：`SkillRegistry` **只注入顶层 `SKILL.md`**（不再递归注入 `references/` 与 `*.template.md`），新增本地工具 **`skill_list` / `skill_read`**（限定在技能目录内）供按需读取参考文件——每轮系统提示由 ~30KB 降至 ~15KB；`ssh_snapshot_get`/`refresh` **默认返回各维度概览**（`view=summary`：`status`/`dataChars`/`headline` 关键指标 + `hint`），用 `section=` 取单维度完整数据、`detail="full"` 取全部，避免一次性把 >15 万字符塞进上下文并诱发"绕路用只读命令逐项补齐"的额外工具开销。此外：**工具描述精简**（`agent.compactToolDescriptions`，默认开——发送给模型时把中文长描述截为要点并指向 `mcp_usage_guide`，降低每次请求固定 token；ML 实测 `tools/list` 约 68KB，其中中文描述是主要可压部分）；**资产档案改增量维护**（新增本地工具 `ops_doc_append`/`ops_doc_patch`，技能与系统提示改为"按需读、增量写"，不再每任务整份重写）。并进一步：**大结果落盘句柄**（`agent.spillLargeToolResults`/`spillDir`/`spillRetentionDays`——超限工具结果写入 `%APPDATA%\LitSSH\spills`，主上下文只留预览 + `spill://` 句柄，新增本地工具 `spill_list`/`spill_read`/`spill_grep` 按需分段读取/搜索）；**子代理隔离**（`agent.enableSubAgent`——`run_subagent` 把独立**只读**取证任务放到隔离上下文执行、只回摘要，大日志/多步排查不再占用主上下文）；并提供 **Prompt Caching 指引**（系统提示与工具定义保持稳定前缀，充分利用 provider 端缓存）。
  - **SSH 连接复用池（`connectionPool`）**：MCP 的 SSH 工具不再"每次调用新建连接→执行→断开"，改为**按服务器复用一条连接**（命令执行完不断开、放回池），**空闲 `idleTimeoutSeconds`（默认 300s）自动断开**，并设 `keepAliveSeconds`（默认 30s）防止被 NAT/防火墙静默掐断、掉线自动重连；同一连接串行化。配置经 `SecurityOptionsProvider` 热读取（`enabled`/`idleTimeoutSeconds`/`keepAliveSeconds`/`connectTimeoutSeconds`/`maxPerServer`；`maxPerServer` 默认 1，可设 1~16 让**同服务器并发调用分配到多条连接**）。可在桌面 App「安全设置 → SSH 连接复用」调整（另含「快照刷新最短间隔」）。效果：一轮密集排障/一次快照从"几十次 SSH 登录"降为**1 次登录、空闲后断开**，避免目标机登录日志反复"连-断"触发告警。**命令通道与 SFTP 通道各自入池**（`ssh_list_files`/`ssh_upload_file`/`ssh_download_file` 复用 SFTP 连接）。App 交互式终端不受影响。
  - **批量/目录下载 `ssh_download_files`（工具 51 → 52）**：用**一条 SFTP 连接**下载多个文件或整个目录（`recursive` 递归、跳过软链接防环、深度上限 16），**整批一次人工确认**，避免"下载文件夹"时每个文件重复建连/认证与逐文件审批；`remotePaths` 每个源路径均过 `allowedRemotePaths` 白名单，受 `maxFiles`（默认 200）上限约束（超出 `truncated=true`），结果逐文件返回 `remotePath/localPath/success/size/error`。
  - **批量/目录上传 `ssh_upload_files`（工具 52 → 53）**：与下载对称——**一条 SFTP 连接**上传多个本地文件或整个本地目录（`recursive` 递归），**按相对路径保留子目录结构**（远程逐级自动建目录），**整批一次人工确认**；每个本地路径过 `allowedLocalPaths`、远程目录过 `allowedRemotePaths`，每文件受 `fileTransfer.maxFileSizeBytes`、总数受 `maxFiles` 约束；结果逐文件返回。
  - **AI 助手界面重构**：左侧会话区顶部只保留 **「AI 助手设置」「新建」** 两个按钮；会话列表 **右键菜单** 提供 重命名 / 删除 / **打开工作区文件夹（资源管理器）** / 清空显示 / 导出 Markdown；右侧移除 测试连接、继续、清空显示、导出 Markdown 按钮（**测试连接**移至设置里模型右键菜单）；**发送/停止合并为一个按状态切换的按钮**；**模型选择下拉整合到输入区**；设置窗口左侧顶部只保留「添加」，模型的 删除 / 启用停用 / 测试连接 改为**模型列表右键菜单**，列表用**圆点 + 灰色删除线体现启用/禁用**。
  - **模型与会话记忆**：聊天模型下拉**只列启用模型**；**每个会话记住最后使用的模型**（`agent_sessions.ProviderId`）——切换会话自动恢复该会话的模型，若其**已删除/停用**则回退全局默认、再回退第一个启用模型；设置里模型**右键启用/停用立即持久化**（无需再点「保存」）；关闭设置后重载会保留当前会话历史（不再清空上下文）。长期记忆/RAG 增加**红字提示**：它是全局共享配置（所有模型/会话共用同一 Embeddings 端点/模型/Key），请勿随对话模型改动。另外：模型右键「删除」**弹窗确认后立即生效**；**切换会话/切换模型不再重连 MCP**——`AgentRuntime` 拆分为"MCP 宿主（只连一次）+ 对话会话"，`ResetSession` 仅重建会话（复用已获取的工具/提示/连接），切换会话即时生效且不再重启 MCP 子进程。
  - **AI 助手设置改为双 Tab**：**「助手设置」**（全局：系统提示、技能目录、工作区目录、MCP 路径、上下文/输出、自动摘要、工具描述精简、落盘、子代理、只读、**工具分组**、长期记忆/RAG、会话保留）+ **「大模型设置」**（模型列表 + 每个模型自身的 Endpoint/模型名/Key/temperature/maxTokens/超时/重试/并发），并各自加红字说明"全局共用 / 随模型独立"。杜绝把全局项误当成模型项。
  - **API Key 掩码显示 + 单任务约束**：设置里模型 API Key 改用 `PasswordBox`（圆点显示，随选中模型同步，输入即回写，保存仍走 DPAPI 加密）；**任务进行中不允许切换会话**（切换会被拦截并提示"当前会话任务进行中，同一时间只能有一个任务，请等待或点「停止」"），避免同一时间多任务导致的会话错乱。
  - **界面现代化（Fluent UI）**：引入 **WPF-UI**（lepo.co，MIT）提供 Fluent 外观与自定义标题栏；新增**自研语义 token**（`Themes/Tokens.Dark.xaml` / `Tokens.Light.xaml`）与控件样式（`Themes/Controls.xaml`），**支持 深色 / 浅色 / 跟随系统**（配置 `ui.theme`，在「AI 助手设置 → 助手设置」可切换、系统主题变化自动跟随）；全局窗口统一底色/前景，修正主窗口内联配色；`SyntaxHighlighter` / `MarkdownRenderer` 的代码块与 Markdown 改为**按主题取色**、主题切换时自动重渲染；**AI 助手窗口**重构为 `FluentWindow` 三栏（会话侧栏 / 气泡对话 / 工具步骤卡 / 现代输入区），**AI 助手设置**改为 Fluent 双 Tab（助手设置 / 大模型设置）。
  - **UI 继续打磨**：AI 助手窗口新增**空态引导**、助手卡片**角色头 + 限宽**、**代码块圆角/主题底色**；设置窗口改为**卡片分组 + 表单限宽**、提示改为柔和信息条；**主窗口**改为 `FluentWindow` + 自定义标题栏；**模型自动选择改为优先"可用"模型**（`Model`/`Endpoint` 非空），对话模型下拉只列出可用模型，避免选中空配置模型导致"未配置模型名"。
  - **Phase 3 起步**：主窗口改为**左侧图标导航栏**（数据源/应用/拓扑/安全/审计/配置/MCP说明/AI 助手，含导出/导入）替代顶部菜单；`TopologyWindow` 深色兼容（画布/节点/文字底色 token 化）；其余窗口统一走全局主题 + WPF-UI 控件样式（完整重设计后续）。
  - **Phase 3 完成**：其余窗口（数据源/应用管理、节点关系、审计、快照历史、工具分组、MCP 说明、拓扑管理、文本输入、安全设置）统一改为 **`FluentWindow` + 自定义标题栏**；编辑类窗口（SSH 服务器/数据源/应用）改为**卡片表单 + 主/次按钮**；灰色/告警文字统一 token 化。至此**全部窗口均为 Fluent 外观**。
  - **表格弹窗深色修复 + 窗口布局记忆**：为 `ListView` / `GridViewColumnHeader` / `GroupBox` 增加全局令牌样式，修复深色下**含表格的弹窗（数据源/应用管理、审计、快照历史、节点关系等）数据区与表头仍为浅色**的问题；新增 `%APPDATA%\LitSSH\ui-layout.json` **窗口尺寸/位置/最大化记忆**（主窗口、AI 助手、AI 助手设置）。
  - **表格行与分组框 Fluent 化**：自定义 `ListViewItem` 模板——**圆角行 + Fluent 悬停（半透明）/选中（半透明强调色）**，修正深色下默认浅蓝选中导致文字发白的问题；自定义 `GroupBox` 卡片模板（圆角边框替代默认"蚀刻线"，随主题）。
  - **表头与拓扑窗口**：`GridViewColumnHeader` 自定义模板（悬停/按下用主题半透明色，去除默认浅蓝高亮）；`TopologyWindow` 由普通 Window 改为 **FluentWindow**（深色标题栏），并修正图例文字对比度。
  - **拓扑节点文字对比度**：节点底色为浅色 pastel，深色主题下文字误用浅色前景导致看不清——服务器容器/应用/数据库节点/连线标签统一改为**深色文字**；禁用服务器节点（深色底）保留主题前景色。
  - **拓扑连线属性面板**：由固定浅底（白字看不清）改为**主题卡片**（`AppCardBrush`/`AppBorderBrush`/`AppSurfaceAltBrush` 标题栏 + 主题文字）。
  - **强调色（accent）**：新增 `ui.accent`（预设 系统默认/蓝/紫/绿/橙/红），在「AI 助手设置 → 助手设置」选择；`ThemeService` 覆盖自研 `AppAccentBrush`/`AppSelectionBrush` 并同步 WPF-UI 控件强调色，主按钮/选中高亮随之变化。
  - **表格列排序**：新增 `GridViewSort` 附加行为（点击列头按该列排序，表头显示 ▲/▼，再点切换升降序），应用于**审计（命令/SQL/会话）、快照历史、数据源管理、应用管理**的列表。
  - **拓扑深色节点配色**：节点颜色改为**主题 token**（`NodeServer/App/Db` 背景与边框、`NodeText`）——深色主题下为**深底浅字**、浅色主题为浅底深字；图例、连线标签同步主题。
  - **Tab 与列表观感**：`TabItem` 自定义模板——**选中用强调色下划线 + 加粗**（更醒目）；`ListBox/ListBoxItem` 自定义模板——**去掉边框线**、圆角、悬停/选中用主题半透明底色；会话/服务器/模型列表的容器样式改为继承隐式模板。
  - **UI 复盘修复**：`NodeRelations` 单元格写死的深色前景改主题 token（深色主题下可读）；多处状态/提示文案加 `TextWrapping` 避免字体较大时截断；`TextInputDialog` 改**自适应高度**、去除中部留白、提示换行；MCP 说明列表容器样式继承隐式模板；拓扑关系类型色 token 化。
  - **AI 助手聊天窗口重构（参考 WorkBuddy/Codex）**：用户气泡**悬停才显示时间与 重发/编辑 图标**，助手卡片**悬停才显示 复制 + 用时**（去掉常驻按钮干扰）；加大**用户消息↔助手回复**间距；**集成式输入框**——圆角卡片 + 占位提示「今天帮你做些什么？ @ 添加上下文 / 调用技能与指令」+ 底栏（`＋` 更多菜单 / 模型选择 / **发送·停止圆形图标按钮**，随运行状态切换图标）。
  - **聊天窗口细节优化**：hover 显示的操作/时间改为**常占位（仅切换透明度，不下撑布局）**；**输入区默认更高**并支持**拖动顶部把手调整高度**（Enter 发送 / Shift+Enter 换行提示）；「轮次/上下文 token」改为**图标**（悬浮查看详情），移除常驻底栏状态文案；模型选择框左侧新增**连接状态圆点**（就绪=绿 / 连接中=黄 / 失败=红），悬浮模型选择显示**状态与工具数**。
  - **聊天细节再优化**：用户气泡**只包消息**，时间/重发/编辑移到**气泡外一行**（常占位不撑开、与气泡样式分离）；输入框占位精简为「今天帮你做些什么？」；拖拽把手改为**纯透明模板**（去掉横线）；新增环形进度控件 **`ContextRing`**——按 `当前tokens / 上下文上限tokens` 显示**环形占用百分比**（≥60% 黄、≥85% 红），悬浮查看详情。
  - **会话侧栏与间距**：左侧改为**图标 + 文案列表**（新建任务 / AI 助手设置 / 打开工作区，参考 WorkBuddy），下方为「会话 (N)」计数标题 + 会话列表；用户气泡与上一条消息间距加大、与助手回复间距减小；环形进度图标与左侧 `＋` 间距加大。
  - **助手标题栏与侧栏微调**：「设置」（图标 + 文案）按钮移入**标题栏**（与最小/最大/关闭同排），去掉标题栏的「AI 运维助手」文案；左侧操作**仅保留** `新建任务` / `打开工作区`（透明无边框）；`搜索会话` 框移到 **「会话 (N)」** 标题下方。
  - **标题栏设置按钮与悬浮反馈修复**：自绘标题栏内的「设置」按钮加 `WindowChrome.IsHitTestVisibleInChrome`（此前被拖拽区吞掉点击、点不开）；新增 `ActionRowButton` 样式（透明/圆角/**悬停与按下底色**），`新建任务`/`打开工作区`/`设置` 均有明显悬浮反馈。
  - **标题栏设置按钮修复（可点/hover）**：把「设置」按钮放入标题栏**可交互区**（`WindowChrome.IsHitTestVisibleInChrome`），该区**自行处理拖动窗口与双击最大化**——解决此前点击被窗口拖拽区吞掉、悬浮无反馈的问题。
  - **会话时间 / 模型入口 / 设置窗口观感**：会话列表每项**右侧显示相对活跃时间**（刚刚 / x分钟前 / x小时前 / x天前 / 日期），标题过长省略不遮挡时间；模型下拉**末尾固定「模型设置」**，点击直接打开设置窗口的 **「大模型设置」Tab**；修正设置窗口**文字偏粗**（隐式 `TextBlock` 常规字重）与**表单右侧留白**（卡片撑满宽度）。
  - **会话右键行为**：右键**不再选中/切换会话**（ListBox 层拦截并定位鼠标所在项），右键菜单的 **重命名 / 删除 / 清空显示 / 导出 Markdown 全部作用于该会话**（非当前会话导出时从库读取）。
  - **图片 / 文档附件**：输入区 `＋` 菜单「添加图片/文件…」、**拖拽文件**、**Ctrl+V 粘贴图片**；附件以芯片展示可移除。**文档**（txt/md/log/json/yaml/xml/csv/代码、`docx`、`pdf`）在客户端**抽取为文本**随指令发送，**超过 8000 字符自动保存到工作区**并在消息里给出 `ops_doc_read(path=…)` 路径（模型按需读取，省 token）；**图片**在 `provider.supportsVision` 开启时以 `DataContent` **多模态**随消息发送，未开启则提示"已忽略"。`AgentSession` 新增多模态 `SendAsync(IReadOnlyList<AIContent>)`。
  - **附件交互完善**：`＋` 菜单拆分为 **「添加图片…」/「添加文件…」**；**拖拽文件**与 **Ctrl+V 粘贴图片**改为**整窗生效**；附件芯片中图片显示**缩略图**，点击图片（芯片或聊天气泡内缩略图）打开**大图预览**（滚轮缩放）；用户气泡展示图片缩略图；会话标题含图片时自动显示 **`[图片]`** 占位（会话列表不显示真实图片）。
  - **附件持久化 + 剪贴板类型处理**：`Ctrl+V` 按剪贴板类型处理——**文件**（资源管理器复制）/ **图片**（含浏览器复制的 PNG 数据流）→ 加为附件，**纯文本** → 正常粘贴；拖拽/粘贴提升到**整窗生效**。附件**落盘到工作区** `attachments/`（图片复制原文件、文档写解析文本），会话消息以**相对路径标记**持久化；**重开会话可从标记还原图片缩略图**（`ImageThumb` 控件），源文件被删则显示 **“图片已删除”** 占位。
  - **重发/编辑携带附件 + 用户消息复制**：**重发**与**编辑并重发**会从会话持久化的附件标记**重建输入**（图片=多模态数据块、文档=工作区文本），附件不再丢失；编辑时输入框载入**纯文本**（不含附件标记行）、附件**同时载入芯片区**（可再增删）；若输入区已有内容会**先确认是否覆盖**。用户消息 hover 新增**复制图标**：**纯文字**→复制文字、**纯附件**→复制附件、**文字+附件**→弹出**「复制文字 / 复制附件」**选择；单张图片复制为图像，多附件复制为文件列表。
  - **会话归档 + 删除二次确认**：会话右键菜单新增 **「归档」**——归档后**移出会话列表**（当前会话被归档会自动切到下一个/新建）；在 **「AI 助手设置 → 归档记录」** 标签页可查看已归档会话并 **「恢复到会话区」/「删除」**；归档会话**受保留策略保护**（不参与按天数/会话数裁剪）。会话列表右键 **「删除」改为二次确认**（不可恢复提示），不再直接删除。
  - **主界面重构（导航化）**：左侧图标栏顶部新增 **主页 / SSH 服务器管理 / 数据源管理 / 应用管理** 导航——**数据源管理、应用管理不再弹窗**，改为在右侧主区域以页面形式展示（`DatasourceManageView` / `ApplicationManageView`），**SSH 服务器管理**为主区域完整管理页（`ServerManageView`，顶部仅 **添加/刷新**，其余操作走条目右键菜单，**双击行打开编辑窗口**）；当前页图标高亮。**外观（主题/强调色，全局）、安全设置、工具分组、导入导出配置、MCP 工具说明** 整合为底部 **「设置」** 入口的**多 Tab** 独立窗口（`AppSettingsWindow`；外观由原「AI 助手设置」迁出，因其对整个 APP 生效）。**资产拓扑、审计日志** 仍为独立窗口，但改为**非模态且不置顶/不设 Owner**（不再浮在主界面之上），打开后仍可操作主界面，重复点击会激活已有窗口。
  - **SSH 会话管理 + 主页改版**：**「连接」改为打开独立的「SSH 会话管理」多标签窗口**（`SshSessionWindow`，非模态/不置顶，每个服务器一个标签，复用命令输入/输出、最近活动、测试连接），主页不再内嵌服务器列表与会话。**主页改版为看板**：**快捷入口**（SSH 会话管理 / 数据源 / 应用 / 拓扑 / 审计 / 设置 / AI 助手）、**数据看板**（服务器总数·禁用数、数据源数、应用数，**点击卡片跳转对应页面**）、**AI 快捷提问**（**输入卡片**：多行输入 + 附件（添加图片/文件、清除全部）+ 发送按钮；**回车发送 / Shift+回车换行**；发送时把文字与附件填入 AI 运维助手输入区，**助手已有内容会先确认覆盖**）。修复数据源编辑弹窗提示文案与「写审批」行重叠（Grid 行数不足）。
  - **交互式终端（SSH 客户端基础）**：SSH 服务器管理右键新增 **「打开终端（交互式）」**，在「SSH 会话管理」窗口中打开 **PTY shell 标签**（与命令会话标签共存）。实现了**自包含 VT100/ANSI 终端**（`TerminalModel` 缓冲+解析器、`TerminalView` 自绘控件、`TerminalSessionViewModel`）：支持光标定位/移动、擦除(ED/EL/ECH)、**SGR 颜色/属性**、滚动区域、插入/删除行/字符、**备用屏(?1049)**、自动换行、保存/恢复光标、**滚动回看**（滚轮）；键盘映射 Enter/Tab/Esc/方向键/Home/End/Insert/Delete/PgUp/PgDn/F1-F12/Ctrl+字母；窗口尺寸变化自动 `ChangeWindowSize`。底层经 `ISshService.OpenShellAsync`（SSH.NET `CreateShellStream` + `xterm-256color`）建立独立连接。可运行 `vim` / `top` / `htop` / `less` 等全屏程序。
  - **SFTP 服务器文件可视化（与终端整合）**：交互式终端标签内**左侧分栏**为远程文件浏览器（可「文件」按钮显示/隐藏）：路径栏导航、目录列表（名称/大小/修改时间、列排序）、**上传文件、下载、新建文件夹、重命名、删除、双击进入目录 / 下载并打开文件**；文件操作走独立 SFTP 连接（`RemoteFileBrowserViewModel` + `RemoteFileBrowserView`），命令式操作（mkdir/mv/rm）经 `ssh_execute` 执行。
  - **终端/文件体验增强**：**关闭 SSH 标签二次确认**（关闭整个会话管理窗口也确认）；文件浏览器**默认打开用户 home**（先 `printf $HOME` 定位，避免权限打开根目录失败）；**分栏宽度记忆**（`%APPDATA%\LitSSH\ui-prefs.json`）；**编辑并回传**（下载到临时目录打开，监视改动**自动回传 SFTP**）；**拖拽上传文件/文件夹**（递归，走 `UploadBatchAsync`）；文件列表新增**权限/属主列**（`-rw-r--r-- user/group`）。
  - **服务器/文件操作增强**：SSH 服务器管理**双击行打开交互式终端**（原为编辑）；文件浏览器支持**多选**、**下载所选（含文件夹递归）到文件夹**、**从列表拖出到资源管理器下载**（拖拽时下载到临时目录再交给系统）、**属性对话框**（名称/路径/类型/大小/时间/权限/属主 + 复制路径）、**每服务器记忆上次目录**（键 `ssh.files.lastdir.<id>`，首次仍回退 home）。
  - **文件/终端体验完善**：修复**下载文件夹丢失层级**（相对基准改为“所选路径的父目录”，保留文件夹本身）；**属性/权限对话框重做**（参考宝塔：所有者/用户组/公共 rwx 勾选、权限码、属主/属组、**应用到子目录**、chmod/chown 应用）；**文件/文件夹 复制/剪切/粘贴**（`cp -a` / `mv`）；**右键「切换到所在目录」在终端执行 `cd`**；**拖拽下载防误触**（仅在已选中项上按下并拖动才触发，操作加**重入保护**，避免反复操作卡顿）；终端 **exit/logout 后工具栏「重新连接」**；终端**支持中文输入法**（内置隐藏输入宿主接管 IME 组合输入）。
  - **修复**：终端输入改为在隐藏输入宿主上监听 `TextChanged`（此前 `TextInput` 被 TextBox 内部处理，导致**中英文数字都无法回显**，仅回车有效）；文件列表**右键不再破坏 Ctrl 多选**（仅当右键落在未选中项时才改写选择，多选可正常下载）；**粘贴遇同名项弹窗确认覆盖**（确认后 `rm -rf` 再 `cp`/`mv`，取消则跳过冲突项）；**属主/属组**用一次 `ls -la` 补齐（SFTP 列表不含属主名，此前属性框为空）。
  - **再完善**：终端输入改用 `AddHandler(TextInput, handledEventsToo)`（修复 IME 提交需多按空格/多出空格）；文件列表**按住鼠标拖动不再打回单选**（保留 Ctrl 多选以便整体拖拽下载）；属性**属主/属组改为下拉选择真实账号**（`getent`/`/etc/passwd`），按钮改为**「修改权限」**且失败**弹窗提示**；终端底部新增**独立发送框**（粘贴/输入后回车或点发送直接下发到 SSH，类似 FinalShell）。
  - **终端体验(优化A)**：设置窗口新增 **「终端」Tab**（全局）：**配色主题**（深色/纯黑/Solarized Dark/Dracula/浅色）、**字体/字号**、**光标样式**（block/bar/underline）、**回看行数**、**选中即复制**，保存即时应用；终端支持**鼠标选区 + 复制**（Ctrl+Shift+C / 选中即复制）、**粘贴**（Ctrl+Shift+V / Shift+Insert / 右键）、**清屏**（Ctrl+Shift+K）、**搜索**（Ctrl+F，高亮匹配并可上一个/下一个）。
  - **连接管理器分组(优化B)**：SSH 服务器编辑新增 **「分组」字段**；SSH 服务器管理列表**按分组显示**（分组标题）。
  - **修复**：普通（英文）空格在终端无法输入（现于英文输入法下补发；中文输入法空格仍用于确认）。
  - **运维增强(优化E)**：**批量执行**（SSH 服务器管理页按钮，多选服务器并发下发同一命令并汇总输出）；**命令片段**（设置→命令片段 管理，终端工具栏「片段」菜单一键下发）；**会话日志记录/回放**（设置→终端 可开关，记录到 `%APPDATA%\LitSSH\terminal-logs`，SSH 服务器管理页「会话日志」查看，自动去除 ANSI）；**资源监控小面板**（服务器右键「资源监控」，定时采集 CPU/内存/磁盘/负载）。
  - **高级连接(优化D-部分)**：新增 **端口转发**（SSH 服务器管理页按钮）：支持**本地(-L) / 远程(-R) / 动态 SOCKS(-D)** 转发，定义持久化到 `config.json` 的 `portForwards`，可启停、可「打开时自动启动」（`PortForwardService` + `PortForwardWindow`）。多级跳板代理待后续。
  - **命令片段支持每服务器**：终端「片段」菜单分 **全局片段 / 服务器片段**（当前服务器专属）两段，并提供 **「管理服务器片段…」「管理全局片段…」** 独立管理弹窗（`SnippetEditorView` 复用；存于 `snippets.json` 与 `server-snippets.json`）。设置窗口「命令片段」Tab 现编辑全局片段。
  - **全局异常提示改为同一条只弹一次**（`App` 去重），避免定时/绑定类异常反复弹窗被误认为卡死，仍全部记录到 `logs/app-errors.log`。
  - **会话标签拖拽排序 + 双击重命名**（会话内生效，`ISshTab`）。
  - **独立「SFTP 文件管理」窗口**（服务器右键「SFTP 文件管理」）：**本地 + 远程双栏**（各自导航/新建/删除/双击进入）、中间 **上传→ / ←下载**，底部 **传输队列**（文件带进度、文件夹走批量递归），上传后自动刷新远程列表。终端标签左侧的现有文件面板保持不变。
  - **会话管理空状态/新建入口**：SSH 会话管理窗口在**无标签时**中央显示 **「选择服务器新建连接」**，**有标签时**标签栏右侧固定 **「+」新建连接**；点开下拉列出未禁用服务器，每台可选 **命令会话 / 交互式终端**。
  - **SFTP 修复**：**每台服务器一个独立窗口**（此前复用同一窗口导致打开 B 仍显示 A）；**上传文件夹保留层级**（相对基准改为父目录）；**传输完成后进度条停止滚动**（`IsIndeterminate` 复位）；**队列名称列加宽**（不再截断）；**下载完成后自动刷新本地列表**（上传后仍自动刷新远程）。







  - **输出与工具效率（基于历史会话/审计分析）**：新增 `agent.responseStyle`（`concise` 默认 / `standard` / `detailed`）——系统提示按**运维习惯**要求"结论先行、只讲重点、控制篇幅"以压低输出 token；`snapshot.minRefreshIntervalSeconds`（默认 60s）+ `ssh_snapshot_refresh` 的 `force` 参数——距上次成功快照小于间隔且未 `force` 时**直接返回已有快照**（`status=fresh`），消除同一任务内反复全量刷新（历史审计显示曾 **3.5 分钟刷 4 次**、单日 34 次）；技能/系统提示补充"**多个只读提权检查合并成一条命令**以减少审批次数"。
  - 配置项 `AppConfig.Agent`（`ConfigMigrator` 兜底、`config.example.json` 示例）；发布脚本 `scripts/build-release.ps1` 会把 MCP 服务器内置到 App 的 `mcp/` 子目录。
- **服务器快照工具 `ssh_snapshot_get` / `ssh_snapshot_refresh`**（归入 `ssh` 分组，工具总数 49 → 51）：把服务器整机态势**采集并持久化到独立库** `%APPDATA%\LitSSH\snapshots.db`（快照记录 + 快照事件流水两张表，与审计库分离，便于独立备份/清理），随时按 `serverId` 查询。默认采集四个维度：
  - `resource` 态势：负载/内存/磁盘（含使用率）/CPU 核数与型号/发行版/内核/主机名/IP/运行时长；
  - `portmap` 端口↔进程名/PID/用户↔systemd 服务**三元组 + 程序路径 `exe` + 完整启动命令行 `cmdline`**（`ss` 提取，区分 TCP/UDP、双栈 `0.0.0.0`/`::`/`*` 与 Unix socket；pid 经 `ps -o user/comm` + `readlink /proc/<pid>/exe` + `/proc/<pid>/cmdline` + `/proc/<pid>/cgroup` 补齐属主、可执行文件路径、启动命令与服务归属；`cmdline` 中常见凭据参数 password/secret/token 等自动脱敏）；
  - `nginx_tls` 站点 TLS：优先用**正在运行的 nginx 的可执行路径**执行 `nginx -T`（兼容宝塔等与 PATH 不同的 nginx），保存**完整有效配置 `effectiveConfig`**（含 include 展开），解析 `server_name`/`listen`/证书路径（支持 `{` 换行写法），并汇总**域名列表 `domains`**（每个域名是否 `ssl`、监听端口、关联证书）；`openssl` 取到期日与 SAN（DNS/IP），标出 N 天内即将过期/已过期；未检测到 nginx 时该维度 `skipped`；
  - `systemd` 健康聚合：service 单元按 active 状态计数、失败单元清单与 `is-system-running` 总态；非 systemd 系统 `skipped`。
  - `docker` 容器：`docker info` 守护进程概览（版本/容器与镜像计数/存储驱动/CPU/内存）+ `docker ps -a` 容器清单（名称/镜像/状态/端口/创建时间）+ `docker stats --no-stream` 运行容器资源（CPU/内存/网络/块IO/PIDs）；未装 docker 该维度 `skipped`，装了但守护进程不可用则 `available=true/daemonRunning=false`（降级）。
  - `security` 安全巡检（只读）：SSH 有效配置（`PermitRootLogin`/`PasswordAuthentication`/`PermitEmptyPasswords`/端口等）、防火墙暴露面（`ufw`/`firewalld`/iptables 规则数）、`fail2ban` 是否安装/运行、MySQL 匿名账户与远程 root、**可远程登录的高权账户**、系统空口令账户、sudoers `NOPASSWD`，并结合监听端口识别**公网暴露的高危端口**（3306/5432/6379/2375/9200/21/23…）；统一输出 `findings[]`（severity/id/title/detail/evidence）与 `summary` 计数。**MySQL 账户核查优先用"与该服务器匹配的已配置 MySQL 数据源凭据"查询 `mysql.user`**（回环 host + 同隧道服务器，或数据源 host == 本服务器 host/IP）；账号不必是 root，但需有 `mysql.*` 的 SELECT 权限，否则 `checked=false` 并说明原因；无匹配数据源时回退免密 best-effort（`auth_socket`/`~/.my.cnf`/`debian.cnf`）。
  - `resource` 增强：**阈值告警**（`warnings` 列表 + `riskLevel`：内存/swap/磁盘/负载按阈值判定，如 swap ≥50% 告警）与 **top5 进程**（`topByCpu`/`topByMemory`，含 pid/用户/CPU%/内存%/RSS/命令行，已过滤内核线程）；`portmap` 将 Unix socket 分为系统级（`unixSockets`）与**桌面/用户会话（`unixSocketsDesktop`，默认折叠**：gnome/pipewire/dbus/X11/ibus 等），主列表只突出网络监听 + 系统级 socket。
  - **默认只读本地快照不连服务器**；`ssh_snapshot_refresh` 为**同步阻塞**采集，描述已提示"耗时较长（典型 10~30 秒，弱网更久）"；**单飞限流**——同一服务器同时只允许一个快照，重复调用立即返回 `status=snapshot_in_progress`（含进行中的 `snapshotId`），不排队。**失败也落库**（`state=failed`，保留已采集到的部分 section 与失败事件；连接失败/超时/取消都会正确收尾，不留 `running` 孤儿）。
  - **提权可配置**：`config.json` 新增 `snapshot.useSudo`（默认 `true`）——开启且服务器配置了 `SudoType` 时自动以 sudo/su 执行**内置固定只读命令**（root 视图更完整），**不逐次弹审批**；关闭或未配置时自动降级（相关字段缺失并标注 `degraded`）。采集命令仍受命令过滤器 `Blocked` 规则约束。
  - **可扩展 + 可保留**：采集维度实现 `ISnapshotCollector` 并在 `Program.cs` 注册即可扩展（为后续趋势图/定时任务预留统一历史 `data`）；`snapshot.retentionPerServer`（默认 30，`0`=不限）按服务器保留最近 N 份并级联清理事件。
  -   每次刷新写一条审计记录（`category=probe`）；快照详情与事件在快照库内独立留存。测试期快照库格式版本为 **v2**：版本不一致时直接重建（不做历史迁移，旧快照自动清空）。
  - **返回视图（降低 token）**：`ssh_snapshot_get`/`ssh_snapshot_refresh` 新增 `section`、`detail` 参数——**默认 `data.view=summary` 只返回各维度概览**（`status`/`durationMs`/`error`/`note`/`dataChars`/`headline` 关键指标），不再默认回全量 `data`；`section="resource|portmap|docker|nginx_tls|systemd|security"` 取某维度完整数据，`detail="full"` 才返回全部；`SnapshotTools.BuildDataView` 对超长字符串/数组做**硬上限压缩**并提示按需拉取。

### 桌面 App

- **服务器列表右键新增「采集快照」与「快照历史」**：采集为同步操作（典型 10~30 秒），完成/失败后自动打开历史窗口并定位本次快照；**单飞限流**——同一服务器同时只允许一个采集（App 内全局忙标志 + 服务内存锁 + 库内 `Running` 唯一部分索引，**跨 App / MCP 进程**也生效），重复触发返回进行中提示。
- **新增「服务器快照历史」窗口**：通用查看页，顶部可**切换服务器**；左列历史快照列表（时间/状态/耗时/提权/错误），选中后右侧展示该次快照的**采集维度概览**（维度/状态/耗时/说明）、**采集事件**流水与**原始数据 JSON**。
- **安全设置新增「服务器快照」区**：采集是否提权（`snapshot.useSudo`）、每服务器保留份数（`snapshot.retentionPerServer`，0=不限）、采集超时秒数（`snapshot.timeoutSeconds`），读写 `config.json` 的顶层 `snapshot` 段。
- **设置窗口新增「关于」Tab**：介绍项目定位与 MCP / 桌面 App 的核心能力，顶部注明 **GitHub 开源地址**（https://github.com/joolan/LitSSHmcp）与 **MIT 许可**。
- **跨机文件复制（源→目标，走服务器间内网通道）**：SSH 会话管理的**远程文件面板**支持多选后 **右键「复制到远程服务器…」**——独立弹窗：左侧为待复制列表，右侧选择**其它服务器**（可切换 **主机地址 / 内网地址**）并浏览目标目录，确认后由**源服务器直接推送**到目标（优先 **`rsync`**，退化 **`tar | ssh`**，可 **`-z` 压缩**），数据**不经本机磁盘**、利用服务器间内网高速通道；进度实时显示（rsync 百分比 / pv）。**认证**：优先源机免密，其次 **`sshpass` + 目标密码**（密码经命令 **stdin** 写入源机 0600 临时文件，**不进命令行 / 日志**）；源机缺 **`sshpass`** 时**弹出安装引导框**（探测源机包管理器，按发行版给出 `apt-get`/`yum`(含 EPEL)/`dnf`/`zypper`/`pacman`/`apk` 安装命令，可一键复制），安装后可点「已安装，重试」或改用内存中转；源机缺 `rsync`/`tar` 或认证不可用时**默认报错**；勾选「直连失败时询问是否内存中转」后，会**弹窗让用户确认**是否改用**经本机内存中转**（SFTP 读→写，不落盘），Core 不自动回退。传输完成后**自动刷新目标服务器文件列表**；「覆盖同名」对 **rsync（`--ignore-existing`）与 tar（`--skip-old-files`）均生效**，取消覆盖时会**在目标端预统计并提示「跳过 N 个同名文件」**（目标 tar 不支持 `--skip-old-files` 时自动改走内存中转以保证不覆盖）。服务器配置新增可选 **内网地址 / 内网端口**（`SshServerConfig.InternalHost` / `InternalPort`，跨机复制时优先内网地址）。另在 **SSH 服务器管理条目右键新增「远程互传（服务器↔服务器）…」**：打开独立窗口，**左右两侧均为可切换的服务器文件浏览器**（左侧现场多选源文件、右侧选择目标服务器与目录；任一侧切换后另一侧自动排除该服务器），其余选项/进度/跳过统计/内存中转与上文一致。
- **会话标签/卡片可拖出为独立窗口**：SSH 会话窗口在**标签布局**下拖动标签、在**平铺（卡片）布局**下拖动卡片头把手，**拖离窗口范围后松手**即把该会话**分离为一个独立、紧凑、可缩放、可自由摆放的窗口**（位置/尺寸按会话记忆，见 `ui-layout.json`）。独立窗口右上角提供 **「收回」**（送回主会话窗口）与 **「✕ 关闭会话」**；标签/卡片右键亦提供 **「分离为独立窗口」**。终端连接状态由会话 VM 持有，**分离/收回不重连、不断开**；主窗口只显示未分离的会话（`DockedSessions`），关闭主窗口会一并关闭独立窗口。
- **主页快捷入口精简**：移除「数据源管理 / 应用管理」（改由下方**数据看板卡片**点击进入），减少重复入口。
- **AI 助手斜杠命令 + 临时聊天 + 压缩会话**：输入框支持**斜杠命令**，面板**悬浮于输入框上方**（不占用输入框布局）、条目单行、↑/↓/Enter/Tab/Esc 操作。
  - **`/临时聊天`**：回车打开**独立的临时聊天面板**——纯聊天、**不注入任何技能 / MCP 工具**、**不持久化（仅内存）**、**可切换模型（保留已聊内容）**、支持附件；**关闭面板即清空**，且与主运维任务**互不影响**（主任务进行中亦可使用）。
  - **`/压缩会话`**：立即压缩当前会话上下文（`AgentSession.CompactNowAsync`：较早轮次摘要后丢弃、保留最近 2 轮）；任务进行中会提示稍后再试。
  - **`/清空屏幕`**：只清空屏幕显示（保留会话上下文 / 模型记忆，不影响运行中的任务）。
  - **`/清空上下文`**：只重置模型上下文（保留屏幕显示与已存记录，**不删除会话记录**）。
  - **`/只读`**：会话级只读模式（仅保留只读工具，`IAgentTool.ReadOnly`）；**再执行一次或点输入区「只读」标签的 ✕ 退出**。
  - **`/工具`**：弹出**工具分组多选对话框**（不勾选=不限制；也支持直接输入 `/工具 log mysql`）；点输入区「工具」标签的 ✕ 退出。`AgentRuntime.SetToolSelection` 重建会话但保留上下文、不重连 MCP。
  - **会话限制写入系统提示 + 子代理**：只读 / 工具分组限制会追加到系统提示（并同步给子代理），**即使上下文被压缩也保留**——引导模型不调用不可用工具、不臆造结果、给出替代建议（缓解只读下“卡住/空转”）。
  - 斜杠命令面板键盘 ↑/↓ 移动时**自动滚动**到可见区。
  - **只读/子代理加固**：只读（设置里的**全局只读**或会话 `/只读`）与 `/工具` 限制均写入系统提示并随压缩保留；设置页「只读模式」下新增**只读工具清单说明**；`run_subagent` 加固——子代理轮数上限压到 8、**同一任务重复委派超过 2 次直接阻止**、无输出时返回“最后动作/错误”以阻止主模型重试空转。
- **关闭主窗口时可选「最小化到托盘 / 退出程序」**：点右上角关闭弹出选择——**最小化到托盘**（后台继续运行，SSH 会话 / AI 任务不受影响；双击托盘图标恢复主界面，右键托盘菜单可「显示主界面 / 退出程序」）；**退出程序**（断开所有 SSH 会话）。基于 WinForms `NotifyIcon`（App 项目启用 `UseWindowsForms`，并移除其隐式 using 以避免与 WPF 类型冲突）。
- **文件/文件夹同步（单向 · P1）**：SSH 服务器管理页新增 **「文件/文件夹同步」** 按钮 → 独立窗口可创建多个任务，方向支持 **本地→远程 / 远程→本地 / 远程→远程**（**仅单向**）；默认**只新增/更新、不删除**，「镜像删除目标端多余文件」为**显式选项**；**「测试」为可选自检**（实际传输一个探针文件验证连通与认证；**不再作为保存的前置条件**，保存仅校验必填项）；调度支持 **固定间隔 / 每周固定时刻 / 每月固定时刻**；失败按**指数退避重试（≤5 次）**并写 **`%APPDATA%\LitSSH\logs\sync-YYYYMMDD.log`**（**不进审计**）；**仅 App 运行期间执行，关闭 App 即停止（不补跑错过的计划）**。自研基于 SFTP 的比对/传输引擎（`size + mtime`，临时文件 + rename 原子落盘）。配置新增 `syncTasks`（schemaVersion 1→2）。
  - **远程↔远程改为服务器端 `rsync` 直连**（`rsync -a --partial [--delete] -e ssh`）：走服务器间内网、不经本机；**测试会前置校验源/目标两端是否安装 rsync**，缺失时给出**对应端的发行版安装命令**（apt/dnf/yum+EPEL/zypper/pacman/apk）；测试为可选自检，失败不影响保存（但运行时同样会因缺少 rsync 而失败）。
  - **上次运行时间/结果落盘**（`%APPDATA%\LitSSH\sync-state.json`）：UI 展示「上次」，并作为「固定间隔」的基准（重启后延续）。
  - **方向联动**：选择同步方向后只展示需配置的项（本地/源/目标按方向显隐）。
  - **镜像删除**增加含义说明：目标端存在、源端不存在的文件/文件夹会被删除（默认不删）。
  - **调度改为三选一**：行1 固定间隔（每隔 X 分钟）；行2 每周固定时刻（**多选周几** + 时刻）；行3 每月固定（**多选日期 1-28** + 时刻）。
  - **路径可选取**：本地路径提供**「选择文件夹」**（本机文件夹对话框）；源/目标远程路径提供**「浏览远程」**（浏览该服务器目录后回填当前目录）。
  - **远程↔远程「使用内网地址」**：勾选后源机经目标的**内网地址**连接（需目标已配置内网地址，否则回退主机地址）；**目标 rsync 能力探测仍走主机地址**（本机可达），仅认证探测与 rsync 命令改用内网地址。
  - **测试失败给出免密配置命令**：认证不通过时直接给出可在**源机复制执行**的命令（`ssh-keygen` / `ssh-copy-id -p 端口 用户@目标` / `ssh -o BatchMode=yes … true` 验证），并保留「安装 sshpass + 目标配密码」的替代路径。
  - **结果改模态弹窗且可复制**：**测试 / 保存 / 立即运行**的结果以**模态弹窗**（`ResultDialog`）展示，正文可选可复制（多行命令、安装提示不再只能看底部状态栏）。
  - **文件过滤（glob）**：任务可设 **「仅同步」/「排除」** 模式（匹配相对路径或文件名，支持 `*` `?`）；本地↔远程按过滤后的文件集传输，远程↔远程转为 rsync `--include='*/' … --include=… --exclude='*'` / `--exclude=…`。

### 修复

- **临时聊天发送推理模型时界面卡死**：临时聊天此前对模型流式输出**逐块**刷新，每个增量都触发 Markdown 富文本**全量重渲染**，推理输出量大时把 UI 线程压死（界面无响应、「停止」也点不动）。现改为与主助手一致的**按时间批量刷新**（80ms 节流，结束时补刷），界面保持流畅、可随时停止。
- **`nginx_tls` 有效配置被 su 交互式 PTY 的登录噪声污染**：`su` 通道机的 `effectiveConfig` 会混入登录 banner（`Last login…`）、命令行回显（`su - root -c '…'`）、`Password:` 提示与 ANSI 转义码（`sudo` 非 PTY 通道本就干净）。现按首个 `# configuration file` 头截断并剥离 ANSI/控制字符，得到干净的有效配置。
- **`mysql_diagnostics` 报"格式错"**：`SHOW FULL PROCESSLIST` 解析用了错误列序（把 `Host` 当 `db`、把 `Command`（`Query`/`Daemon`/`Sleep`…）当 `Time`），`Convert.ToInt64("Daemon")` 抛 `FormatException` 导致整个诊断失败、只能绕道 `mysql_query`。现按正确列序（`Id,User,Host,db,Command,Time,State,Info`）取值，并以不抛异常的数值转换兜底；`byDatabase` 与"最长运行查询"统计同步修正。
- **浅色主题主按钮（强调色底）文字发黑**：`Controls.xaml` 的全局隐式 `TextBlock` 样式设置了 `Foreground`，其优先级高于内容控件内部文本的继承值，导致 `ui:Button Appearance=Primary`（以及 ComboBox 等）内部文字被强制为浅色主题的深色前景。移除全局 `Foreground` setter（前景改由 `FluentWindow` 的 `WindowForeground` 继承，随主题变化），并为 `AccentButtonForeground`/`PointerOver`/`Pressed` 统一白色前景，修复"蓝底黑字"。
- **跨机复制：兼容旧 OpenSSH + 两端能力探测 + 不阻塞主程序**：① 源机为旧版 OpenSSH（如 CentOS 7 自带 7.4）时不支持 `StrictHostKeyChecking=accept-new`（OpenSSH ≥ 7.6 才有）而报 `unsupported option "accept-new"`，改用兼容的 `StrictHostKeyChecking=no`；② **rsync/tar 需源机与目标机两端都存在**，此前只探测源机，目标机无 rsync 时报 `rsync: remote command not found`——现**同时探测目标端**，任一端缺失自动退化 `tar|ssh`，两端都缺则提示/内存中转；③ 将「复制到远程服务器」窗口改为**非模态**，其中转确认框 / sshpass 安装引导框仅**模态于该窗口**，不再阻塞 App 主程序。

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
