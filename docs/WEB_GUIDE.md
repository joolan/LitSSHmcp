# Web 助手使用指南

本文档详细介绍浏览器端 AI 运维助手、启动方式、安全边界与账号管理。

基于浏览器的 AI 运维助手（`LitSSHmcp.Web`，ASP.NET Core + WebSocket），与桌面 App / MCP 服务共用同一套 Core 服务、安全策略与 `config.json`：**每个 AI 会话运行在独立子进程**（进程隔离、互不影响），支持流式聊天（思考 / 工具调用 / 结果分帧推送）、**图片附件**（粘贴 / 拖拽 / 选择，多模态输入）、**会话级模型切换**、Web 内审批卡片（**固定悬浮面板 + 顶栏「待审批 N」徽标**，`/ws/approvals` 实时推送 + REST 定时对账自愈，REST 决策）、服务器目录浏览与文件下载、会话生命周期管理（删除即终止子进程）与并发 / 成本预算约束。输入区与桌面一致：**发送 / 停止合并为一个按钮**，模型选择与图片上传在输入框下方同一工具行；界面为**响应式布局**（桌面居中会话列 + 手机抽屉侧栏 / 底部输入栏 / 底部审批弹层）；**历史分页懒加载**（首屏最近 120 条 + 「加载更早」）；**冷启动上下文与桌面共用同一重建逻辑**（冷启动窗口 / 工具轨迹 / 会话智能体 / 技能 / 计划模式 / 资产图谱 / 视觉图片；Web 仍不含本机终端工具）；Markdown 支持表格、任务列表与代码块复制，工具轨迹渲染为可折叠卡片，**历史图片附件显示缩略图**。桌面端仅在 AI 页面可见、且没有本地会话运行/编辑时对账 `agent.db`：刚有变化时 5s 一次，空闲自动降到 20s，页面重新可见会立即补一次；会自动刷新 Web 进程写入的会话列表与选中会话结果（跨进程只能同步已落库的整轮结果）。安全加固见 [Web 助手安全](SECURITY.md)。

**2026-10 新增**：顶栏「运维视图」（事件 / 时间线 / 资产画像）、「长任务」面板（任务列表 / 状态进度 / 最近事件 / 结果查看 / 取消 / 终态提示）；桌面左侧栏提供「批量变更」窗口，Web 端批量变更入口暂时隐藏（保留实现与后端链路）；批量变更沿用当前会话的 MCP 审批与写锁，不绕过 canary。

**告警接入**：设置环境变量 `LITSSH_ALERT_WEBHOOK_TOKEN` 后，Alertmanager 可 `POST /api/alerts/alertmanager`（请求头 `X-LitSSH-Alert-Token` 或 `Authorization: Bearer`）；按 `hostname` / `instance` / `serverId` 等标签映射 SSH 资产，`firing` 创建团队事件、`resolved` 取代匹配的 open 事件，重复 `firing` 幂等跳过。可用 `LITSSH_ALERT_OWNER_USER_ID` 指定事件归属（默认 `alert-webhook`，写入团队作用域）。

**配置生效边界**：**安全类配置**（`security.enabled`、命令 / SQL / 脚本过滤、审批设置、授权矩阵等 `security.*`）按 `config.json` 的 mtime **热加载即时生效**（无需重启，对运行中会话同样生效）；**其余配置**（服务器 / 数据源 / 模型 / 技能 / 连接器注册等）在**会话启动时快照**——修改对运行中会话**不生效、下个会话生效**。Web 不提供任何配置写入口（仅任务级选择），一切配置修改在桌面 App 完成。

## 启动

```bash
dotnet run --project src/LitSSHmcp.Web                # 开发启动 → http://localhost:5000

# 发布
dotnet publish src/LitSSHmcp.Web -c Release -r win-x64 --self-contained -o publish-web
```

- HTTP 默认**只绑定本机回环**；跨机访问请显式设置 `ASPNETCORE_URLS`（如 `http://0.0.0.0:5000`），并注意 [HTTP 明文风险](SECURITY.md)。
- 配置了 `web.https`（PFX 证书）时自动追加 HTTPS 监听（默认端口 7443，**绑定所有网卡**，见 [Web 助手安全](SECURITY.md)）。
- **Web 会话工作区根**默认 `%APPDATA%\LitSSH\jobs`；可在 `config.json` 用 `web.jobsRoot`（**绝对路径**）调整，**重启 Web 后生效**。会话目录为 `<根>\<用户Id>\s<会话Id>`，文件浏览 / 下载仍强制约束在该根之下；会话删除即清理目录，**孤儿目录**（删除失败 / 会话被保留策略剪除后残留）按 `agent.orphanSessionDirRetentionDays`（默认 30，0=关闭）在启动时清扫。

## 账号管理

Web 使用**独立账号库** `%APPDATA%\LitSSH\web.db`——**没有内置默认账号**，用命令行创建 / 查看（密码只经环境变量传递，不进命令行历史与进程列表）：

```powershell
# 创建账号（首个账号自动为 admin，之后默认 user；密码 ≥8 位，建议用 --role 显式指定）
$env:LITSSH_NEW_PASSWORD = "<密码>"
src\LitSSHmcp.Web\bin\Debug\net8.0\LitSSHmcp.Web.exe --create-user <用户名> --role admin

# 重置账号密码（同时吊销全部登录令牌、清零失败计数并解除锁定）
$env:LITSSH_NEW_PASSWORD = "<新密码>"
src\LitSSHmcp.Web\bin\Debug\net8.0\LitSSHmcp.Web.exe --reset-password <用户名>

# 查看全部账号
src\LitSSHmcp.Web\bin\Debug\net8.0\LitSSHmcp.Web.exe --list-users
```

> 账号带 `admin` / `user` 角色字段（首个账号自动 admin，密码最短 8 位）；普通成员按**登录态 + 会话 owner 归属**访问，管理员能力（`allUsers` 全局历史/会话、共享工作区/脚本写、团队 Memory、全局拓扑）在服务端按角色校验；当前不提供独立 Web 管理 UI。连续登录失败 5 次起按指数退避锁定（5 分钟起、封顶 1 小时）。同一账号库也被桌面「AI 助手 → 侧栏登录」用于**归因**（不影响权限）。
