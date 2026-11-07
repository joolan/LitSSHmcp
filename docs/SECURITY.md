# 安全模型与提权

本文档汇总应用锁、审批、命令 / SQL 过滤、凭据隔离、Web 助手安全和 sudo 提权配置。

## 安全模型

- **应用锁（启动密码门卫）**：可为本程序设置启动密码（设置 → 安全设置 → 应用锁），每次打开需输入密码或用 **Windows Hello**（指纹/PIN）解锁才能进入主界面；跟随 **Windows 锁屏**（Win+L 离开后回来重新上锁）；密码经 **PBKDF2-SHA256** 校验、哈希经 DPAPI 加密落盘（明文不落盘），解锁/设置结果写入审计日志。定位是**防他人随手拿到电脑直接操作的门卫，不是凭据金库**（凭据本身仍由 DPAPI 保护；忘记密码可删除 `config.json` 中 `appLock` 节点重置）
- **全局开关**：安全设置中的「启用 MCP 服务」关闭后**拒绝所有工具调用**（`security.enabled`，按配置热生效、无需重启）
- **禁止命令列表**：直接拒绝执行危险命令
- **敏感命令列表**：弹出桌面窗口提示用户确认后执行
- **授权确认弹窗**：置顶确认框，默认 **45 秒无操作自动拒绝**；可选独立子进程 / 原生弹窗样式（`security.approval`）
- **审批通道与模式**：`security.approval.channels` / `security.approval.mode`（`manual` / `auto-approve` / `auto-reject`）
- **文件传输审批**：上传 / 下载需用户确认，并受**本地 / 远程路径白名单**与大小上限约束（`allowedLocalPaths` / `allowedRemotePaths` / `maxFileSizeBytes`）；单文件 / 多文件 / 文件夹统一用 `ssh_upload` / `ssh_download`（一条 SFTP 连接、整批一次确认、保留子目录层级）
- **主机密钥校验 (TOFU)**：首次连接记录 SSH 主机指纹，之后指纹变化即拒绝（`security.sshHostKey.mode = tofu|strict|off`）
- **按目标限流**：单服务器 / 数据源的并发数与每分钟调用上限（`security.limits`）
- **SSH 连接复用池**：每服务器复用一条 SSH 连接、执行完不断开、空闲自动断开（`connectionPool.*`；默认开、空闲 300s、每服务器 1 条），避免 AI / 密集调用反复"连-断"触发目标机登录告警
- **凭据隔离**：数据库 / SSH 账号密码仅保存在本机（DPAPI 加密落盘），任何 MCP 工具的入参与出参都不包含密码，AI 智能体只能通过 `datasourceId` 引用数据源
- **SQL 安全过滤**：`mysql_query` / `postgres_query` 仅允许只读语句；写语句中无 WHERE 的 DELETE / UPDATE、DROP TABLE / DATABASE、GRANT 等直接拒绝，其余敏感写语句需桌面确认
- **Redis 安全策略**：`redis_read` 只放行只读白名单命令；`redis_execute` **所有写操作一律桌面审批**，FLUSHALL / SHUTDOWN / DEBUG / SUBSCRIBE 等危险与阻塞类命令直接拒绝
- **Docker 安全规则**：默认拒绝 `docker system prune` / `docker volume rm` / `docker network prune` / `docker run --privileged` 等；`docker rm/rmi/kill/stop/restart/run/exec/compose down` 等写操作需桌面确认（只读的 `docker ps/logs/inspect/stats` 不受限）
- **审计日志**：记录所有命令与 SQL（含被拒绝的），支持 SQL 原文开关、字面量脱敏、**超期记录归档到历史表永久保留**；落盘前统一脱敏密码/token/连接串/私钥等敏感内容（命中追加“敏感内容已自动脱敏”标记），审计写入 **HMAC-SHA256 哈希链**，可在「审计日志」中**校验完整性**检测篡改（`security.audit`）；每条记录带 **MCP 会话 ID**，便于按会话区分
- **提权执行**：权限不足时可使用 sudo 提权（见 [提权配置](#提权配置)）

### Web 助手安全

- **认证**：独立账号（PBKDF2-SHA256 210k 迭代），密码最短 8 位；连续失败 5 次起按指数退避锁定（5 分钟起、封顶 1 小时）；令牌 Cookie `HttpOnly + SameSite`
- **授权矩阵**：Web 会话的 MCP 目标工具按「登录用户 → 目标白名单 + read/write/exec 级别」在工具调用过滤层强制校验（`security.authorization`，**未命中规则默认拒绝** fail-closed，拒绝落 `authorization_denied` 审计）；工具目标与最低级别由安全目录统一声明，日志/Docker/Java/拓扑/应用体检等目标工具同样覆盖；可视化配置入口在桌面 **「设置 → 安全设置 → Web 授权矩阵」**（启用开关 + 规则行增删，同一用户可多行分别授权不同目标，保存热生效），无需手改 `config.json`。**数据可见范围**：强制模式下命令 / SQL 历史与会话列表默认只返回本人数据，管理员可用 `allUsers=true` 显式查看全局；`ssh_list_servers` / `datasource_list` 按目标白名单裁剪（并隐藏未授权隧道路由/关联服务器引用）；无目标的全局拓扑查询仅管理员可用；桌面未启用强制时保持全量
- **限速**：登录按 IP 30 次/分钟，另按账号（跨 IP）10 次/分钟；`/api/*` 600 次/分钟，超限 429 并落 `rate_limited` / `login_rate_limited` 访问日志
- **会话隔离与配额**：所有会话接口逐请求校验 owner（越权统一 404 不泄露存在性）；每用户并发会话上限默认 8（`security.limits.maxSessionsPerUser`，0=不限）；LLM 成本预算按会话生效（`agent.budget.maxSessionCost`，会话数 × 成本上限即单用户上界）
- **文件下载边界**：会话文件下载单文件上限 100 MiB（超限 413），支持单段 `Range`/206/416，每用户最多 4 个并发下载（超限 429），单次响应 5 分钟超时；流式发送，不整文件读入内存
- **浏览器侧防护**：写操作 CSRF 双提交令牌、WebSocket 握手校验 Origin、全站安全响应头（`nosniff`/`X-Frame-Options: DENY`/`Referrer-Policy` + HTML `Content-Security-Policy`），认证响应 `Cache-Control: no-store`；模型输出 Markdown 经白名单 sanitize 后才渲染（防存储型 XSS）
- **访问日志**：登录成败、会话创建/删除、文件下载、越权/限速拒绝均落盘可查
- **传输（HTTPS 建议）**：默认 **HTTP 局域网明文**——Cookie/令牌可被窃听、聊天内容可被中间人；**仅建议在可信内网使用**。启用 HTTPS：在 `config.json` 配置 `web.https` 节点（PFX 证书），Web 启动即追加 HTTPS 监听（默认端口 7443，可用 `port` 修改；证书密码支持 `enc:` DPAPI 密文），并自动启用 HSTS 与 HTTP→HTTPS 重定向。反向代理部署时，在 `web.trustedProxies` 显式填写代理 IP，才会信任 `X-Forwarded-For/Proto`（为空=完全忽略，防伪造来源 IP/协议）：

  ```json
  "web": {
    "https": { "certificatePath": "C:/certs/litssh.pfx", "certificatePassword": "enc:…", "port": 7443 },
    "trustedProxies": ["192.0.2.10"]
  }
  ```

  建议使用内网 CA 或自签证书，并用防火墙/网卡绑定限制仅内网访问

- **残余风险（明确声明，不承诺完全消除）**：Web 用户可能经**提示注入**诱导 AI 作恶（模型把不可信内容当作指令执行）——已有三重兜底降低概率：**授权矩阵**（最小授权、默认拒绝）+ **敏感操作审批**（写类操作人工确认）+ **命令/脚本过滤**（黑名单硬拒绝、脚本永不免审），系统提示亦内置注入防护（工具输出视为不可信数据）；但该风险**无法归零**，请配合审计追溯（`authorization_denied` 与命令审计均入哈希链）与按需最小授权使用

## 提权配置

| 提权方式 | 说明 | 适用场景 |
|---------|------|---------|
| 不启用 | 不使用提权 | 普通用户操作 |
| 当前用户sudo | 使用 SSH 用户密码执行 sudo | CentOS / Ubuntu / Debian 常用 |
| root用户 | 切换到 root 用户 | 需要 root 密码 |
| 指定用户 | 切换到指定用户 | 需要该用户密码 |
| 自动 | 先试「当前用户 sudo」，失败再试「su - root」（同一提权密码） | 不确定账号是否在 sudoers / 是否配了 root 密码时；推荐 |

**Linux sudo 机制**：

- `sudo command` 需要输入**当前 SSH 用户**的密码
- 如果 sudoers 配置了 `NOPASSWD`，则不需要密码
- root 用户执行 sudo 不需要密码
