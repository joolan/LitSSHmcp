# 使用示例与 CLI

本文档给出典型 AI 提问、审批示例和命令行工具用法。

## 使用示例

### 执行命令

```
在Web服务器上执行 df -h 查看磁盘空间
```

### 提权执行

```
查看nginx进程状态（需要root权限）
```

AI 会自动使用 `ssh_execute_sudo` 工具提权执行。

### 文件传输

```
把本地的 config.yml 上传到服务器的 /etc/nginx/ 目录
下载服务器上的 /var/log/nginx/access.log 到桌面
```

### 数据源与拓扑排查

```
订单服务报错了，帮我排查一下
```

AI 的典型排查链路：

1. `topology_query(action="overview")` 拿到全局拓扑：订单服务（Java）运行在哪台 SSH 服务器、连接了哪个 MySQL
2. 不确定 ID 时先用 `topology_query(action="search")`（名称/主机/端口/标签）找到精确资产，再用 `topology_query(action="dependencies", assetId="app:order-service")` 获取上下游依赖
3. 需要链路/影响面时用 `topology_query(action="path")`（应用→数据源/服务器）、`topology_query(action="impact")`（机器/数据库变更影响谁）、`topology_query(action="explain")`（来源/证据）
4. SSH 侧：`ssh_execute_command` 查看 java 进程、端口、应用日志中的数据库连接异常
5. 数据库侧：`mysql_query(action="diagnostics")` 看连接数 / 慢查询 / 锁等待 / 复制状态，`mysql_query(action="query")` 查 `SHOW FULL PROCESSLIST` 与慢日志，`mysql_query(action="explain")` 分析问题 SQL；涉及缓存时用 `redis_diagnostics` 看内存 / 命中率 / 慢日志、`redis_read` 查具体 key
6. 跨机关联：应用日志里的数据库 IP 与 `datasource_list` 返回的 `host/port` 对应，即可确认是哪个数据源（MySQL 3306 / Redis 6379 皆可匹配）
7. 拓扑过期时用 `topology_discover` 重新采集发现证据与配置建议（扫描 java / 通用服务进程、监听端口、ESTAB 连接、配置文件 JDBC / Redis / RabbitMQ / Kafka / Nginx、docker 容器、MySQL processlist）；发现做了**节流**（同时只跑一个）；未登记的资产/端点会进入发现报告、配置建议和配置体检，**不会生成主图待确认节点**。URL 入口不明确时先用 `topology_query(action="resolve_url", url=...)` 解析落地应用与服务器。

### 授权确认弹窗

敏感操作（敏感命令、敏感 SQL、文件传输）会弹出确认框，默认**置顶**且 **45 秒无操作自动拒绝**。样式由 `security.approval.style` 控制：

- `process`（推荐）：启动独立子进程显示弹窗，规避部分宿主（如 Electron 客户端）的隐藏窗口问题；
- `dialog`：MCP 进程内显示；
- `native`：原生置顶 MessageBox（无超时）。

弹窗提供三档**放行范围**（默认仅允许一次）：勾选「**本轮次内自动放行该工具**」后，同一条用户消息内的后续同工具调用不再弹窗，**发送下一条消息自动失效、恢复正常审批**；勾选「**整个助手运行期间自动放行该工具（不建议）**」则到本次助手会话结束（关闭助手 / 断开连接）为止。`native` 样式不提供范围勾选。

审批**通道**由 `security.approval.channels` 控制（默认 `["desktop","cli"]`）：`desktop`（本机弹窗）/ `cli`（带外，操作员用 `litssh approvals` 查看、`litssh approve/deny <id>` 决定）；可同时启用，**首个给出决定者生效**。结果为 `rejected`（拒绝）/ `approval_timeout`（超时）/ `approval_unavailable`（无可用通道）。

## 命令行工具 (CLI)

LitSSH 提供命令行 SSH 连接工具，可直接在终端连接服务器。

```bash
# 发布
dotnet publish src/LitSSHmcp.Cli -c Release -r win-x64 --self-contained -o publish-cli

# 使用
litssh list                        # 列出所有服务器
litssh connect web-server          # 连接到服务器(交互式)
litssh run web-server "df -h"      # 在服务器上执行单条命令
litssh run my-server "docker ps"

# 带外审批(无桌面/headless 时)
litssh approvals                   # 列出待审批的敏感操作
litssh approve <审批ID>            # 批准
litssh deny <审批ID>               # 拒绝
```
