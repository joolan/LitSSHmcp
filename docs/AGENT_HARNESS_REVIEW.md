# AI 运维助手 Harness 评审与改进设计

本文针对 LitSSH AI 运维助手（`LitSSHmcp.Agent` + `LitSSHmcp.App`）做一次完整梳理：**关程序重开后的上下文恢复 / provider 缓存**核查，以及 harness（工具、轮次、提示词、技能、上下文、图片）的改进设计与落地记录。附录含 provider 缓存模型与图像 token 事实表。

## 1. 重开恢复：现状核查（file:line 证据）

载入链路：`AgentWindow.Loaded → InitializeAsync`（`AgentViewModel.cs:506`）→ 初始化库/裁剪 → 铺开模型 → `ReloadSessionsAsync` → 取最近更新会话 → `LoadSessionAsync`（`:569`）→ 读 `agent_messages` 重建 UI Turns + 模型 history → `ResetRuntimeAsync` → 首次 `EnsureRuntimeAsync` 重连 MCP、重建系统提示、`RestoreSummaryFromStoreAsync` 回填摘要。

**已能恢复**：会话列表/标题/最后模型（`agent_sessions.ProviderId`）、user/assistant 文字、滚动摘要（`Summary` 列）。

**缺口**：

| # | 缺口 | 证据 | 影响 |
|---|---|---|---|
| 1 | 工具轨迹不落库 | `PersistTranscriptAsync` 只写 user/assistant（`:1446`）；步骤仅在内存 `AgentTurn.Steps` | 重开后模型失去取证依据，可能重复调用/臆测；UI 也不显示工具过程 |
| 2 | 历史重建路径不一致 | `LoadSessionAsync` 用 `ParseStoredUser` 剥离标记（`:593`）；`LoadHistoryFromStoreAsync` 用原始 `row.Content`（`:825`）；`RebuildSessionHistoryUpTo` 用 `turn.UserText`（`:1439`） | 同一会话在不同触发下模型看到的历史不同 → 语义与缓存前缀不稳定 |
| 3 | 图片/附件 bytes 不落库 | DB 只有 `🖼/📎` 标记 + 工作区副本 | 重开后图片不再进上下文；文档只回文件名 |
| 4 | 压缩后的内存历史不落库 | 落库是全量 Turns | 重开 = 摘要 + 全量文字，首轮再裁剪 |

## 2. 跨重启的缓存行为

请求前缀顺序：`[tools] → [system] → messages`（Anthropic 明确；OpenAI 工具/开发者消息在前）。

- **tools + system 跨重启字节稳定**（同 MCP exe、同工具顺序、同技能文件、同配置）→ provider 缓存未过期时这段仍命中；DeepSeek 需整单元匹配，通常整段 miss。
- **messages 层**：纯文字无附件会话重建后可逐字节一致（仍有机会命中）；有工具调用/附件时第一个分叉点（tool 结果被剥、assistant 多段被合并）即止步。
- **TTL 硬约束**：OpenAI ~5–10 分钟（GPT-5.6 默认 30 分钟）、Anthropic 5 分钟、DeepSeek 数小时~数天。关停超过 TTL 必然 miss（首轮全价，之后正常）。
- 观测：`UsageDetails.CachedInputTokenCount` 已在顶部显示（命中数/命中率）。

## 3. 优化设计与决策

### 3.1 移除 `/只读` 与 `/工具`（本次落地）

会话级只读/工具分组会**重建 tools 数组**（`AgentRuntime.SetToolSelection`），而 tools 位于缓存前缀最前 → 每次切换都是一次**整段缓存重建**（Claude Code/Anthropic 官方建议：模式不要换工具集）。且只读/分组在全局设置（`agent.readOnly`、`agent.allowedToolGroups` + 工具分组页）中已有等价能力，危险操作还有桌面审批兜底。

**决策**：移除 `/只读`、`/工具` 会话级命令与标签（含 `ToolGroupsDialog`、`SetToolSelection`、会话级 `RestrictionNote`）。全局只读改为**运行时启动时**一次性过滤（MCP + 本地写工具），并写入系统提示；会话期内工具集恒定 → 前缀稳定。

### 3.2 A1 工具轨迹落库

`agent_messages` 新增 `role='tool'` 行（`Content` 为紧凑 JSON：tool/args/result/ok/ms；按 Id 顺序天然插在该轮 user 与 assistant 之间）。重载时：

- UI：还原 `AgentTurn.Steps`（含完整入参/结果/耗时/成功态，可展开、可重试）；
- 模型：在该轮 user 之后注入一条 `System` 文本轨迹（工具/入参/结果摘要截断），给模型"我做过什么"的证据，同时**不伪造 function_call 配对**，避免孤儿工具消息。

`result` 落库按上限截断（默认 20000 字符）；模型轨迹每条 args≤300/结果≤600 字符、每轮总量≤3000 字符。

### 3.3 A2 统一历史重建

新增单一入口 `BuildTranscriptAsync(sessionId)`：同时产出 UI Turns 与模型 history（user 明文 + 轨迹 System + assistant）。`LoadSessionAsync`、`LoadHistoryFromStoreAsync`、`SwitchProvider`、`ReloadProviders` 全部走它；`RebuildSessionHistoryUpTo`（编辑/重发）复用同一轨迹拼装函数，保证"同一会话在任意路径下模型看到的历史一致"。

### 3.4 E1 技能核心章节内联

现状：`SkillRegistry` 对大技能只注入索引（标题/描述/目录），模型每个新会话都必须先 `skill_read`。改进：**大技能内联完整核心章节（preamble + 前置 `##` 章节，累计 ≤ ~4000 字符），其余章节列目录 + `skill_read` 提示**；小文件仍全文内联；`references/` 仍不注入。既保留"开箱即用"的核心流程（安全约定、工具速查、标准排障），又避免 16KB 全文常驻。

### 3.5 本轮第二批落地（性能 / 安全 / 恢复）

- **C1 并行只读工具**：同一轮模型返回多个 `tool_calls` 时，**只读且非破坏性**的调用并行执行（写/需审批/未知工具保持串行）；结果、事件与历史仍按**原始调用顺序**回填，并用 `AgentEvent.CallId` 把结果精确映射到 UI 步骤（`AgentStep.CallId`）。多台/多维只读取证显著降延迟。
- **C4 连续失败熔断**：同一轮内工具连续失败（含审批被拒）达 **4** 次即停止本轮并给出 Error 事件，避免空转烧 token（阈值 `MaxConsecutiveToolFailures`）。
- **A3 图片重载配额**：`BuildHistoryFromTurns` 在视觉模型下从最新往回**最多重建 3 张**历史图片（从工作区副本读取并再次压缩），其余只保留文本标记；`RebuildSessionHistoryUpTo`（编辑/重发）也复用该逻辑。
- **C2 类型感知截断**：日志类工具（`log_tail`、`*_logs`）超限时保留**尾部最新内容**，其余保留头部；落盘预览同样按此策略。
- **A4 计划持久化**：`agent_sessions.Plan` 列存任务计划 JSON，`update_plan` / 新指令清空时保存、加载会话时恢复。
- **D2 上下文预算分解**：`AgentSession.GetContextBreakdown`（系统提示/摘要/**工具定义**/用户/助手/工具结果）与 `TokenEstimator.EstimateTool`（名称+描述+schema）；顶部上下文圆环悬浮显示构成与请求合计（工具定义列为固定前缀、不计入裁剪上限）。
- **E3 工作流规则**：系统提示固化"先只读取证→定位根因→最小变更→变更后验证（说明影响与回滚）"与"同一调用连续失败两次即换策略"，与代码层熔断配合。

### 3.6 后续路线（未在本轮）

- OpenAI `prompt_cache_key`；缓存过期对齐的旧工具结果重写（受 provider TTL 差异影响，暂缓）。
- 运行预算（墙钟/成本上限，`MaxToolIterations` 已有）。

## 4. 专业能力方向

- 场景 runbook 技能包（MySQL 慢查询、Redis 内存、Docker/nginx、磁盘/证书巡检、CI/CD 发布回滚）走"核心内联 + 引用按需"。
- 系统提示固化：诊断优先（先只读后写）、最小变更、失败两次换策略、结论/证据/影响/建议/回滚模板。
- 资产图谱小预算注入（Aider repo-map 思路）。

## 附录 A：provider 前缀缓存模型（事实表）

| provider | 渲染顺序 | 最小前缀 | 命中读价 | 命中观测字段 |
|---|---|---|---|---|
| OpenAI（GPT-4o 起，默认开启） | `tools/schema → system → messages` | 1024 tokens | 约 0.1×（新模型） | `usage.prompt_tokens_details.cached_tokens` |
| Anthropic | `tools → system → messages`（明确） | 512–4096（按模型） | 0.1× | `cache_read_input_tokens` |
| DeepSeek | 从头匹配（整单元） | 未公开 | 约 0.1× | `prompt_cache_hit_tokens` |

**关键规则**：前缀里任何一处变化，**它之后的内容全部失效**；由于 `tools` 定义渲染在最前，**工具数组一变 ≈ 整段缓存失效**。本项目由 `M.E.AI` 把 `cached_tokens` 映射为 `UsageDetails.CachedInputTokenCount`，已在 `RecordUsage` 读取并在顶部显示命中率。

### 变更 → 缓存失效范围

| 变动 | 失效范围 |
|---|---|
| 追加 user/assistant（append-only） | 不失效（理想路径，已满足） |
| 改 tools（增删/改名/描述/顺序） | **整段** |
| 改 system 前缀 | system 及其后 |
| 改摘要 @Messages[1] | system 之后（对话部分） |
| 原地重写老 tool 结果 | 从该消息起 |
| 尾部插入召回/轨迹消息 | 仅尾部 |

## 附录 B：图像发送与 token

- 图片经 `DataContent(bytes, mime)` 由 SDK 序列化成 `image_url` 的 `data:` URI；base64 只是传输编码，provider 转成**图像 token**：OpenAI 约 `85 + 170/块`（2048 见方 → 最短边 768 → 每 512 一块），Anthropic 约 `⌈w/28⌉×⌈h/28⌉`。
- 发送前由 `AttachmentService.DownscaleImage` 按最长边 ≤1536px 缩放（JPEG→JPEG q85，其余→PNG，仅在更小时采用）；`TokenEstimator.EstimateData` 解析 PNG/JPEG/GIF/BMP 尺寸估算图像 token（此前按 0 计）。
- 同一会话内图片会随每次请求重发（落在稳定前缀内可被缓存覆盖）；重启恢复时仅重建最近 3 张（见 §3.5 A3），其余只保留文本标记。
