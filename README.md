# LitSSH MCP

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows-blue.svg)](https://windows.com/)
[![AI Assistant](https://img.shields.io/badge/AI%20Assistant-Opencode-blue.svg)](https://opencode.ai/)

Windows平台下的SSH MCP服务器，让AI智能体可以安全地通过SSH管理远程服务器。

> 🤖 本项目使用 [Opencode](https://opencode.ai/) AI助手开发

## ✨ 功能特性

### MCP Tools (AI可调用的工具)

| Tool | 描述 |
|------|------|
| `list_servers` | 列出所有已配置的SSH服务器 |
| `execute_command` | 在指定服务器执行Shell命令 |
| `execute_with_sudo` | 使用提权执行命令（权限不足时使用） |
| `get_sudo_status` | 获取服务器提权配置状态 |
| `get_server_status` | 获取服务器连接状态 |
| `test_connection` | 测试SSH连接 |
| `get_command_history` | 查看命令执行历史 |
| `upload_file` | 上传本地文件到服务器 |
| `download_file` | 从服务器下载文件到本地 |
| `list_remote_files` | 浏览服务器目录 |
| `get_usage_guide` | 获取使用指南 |

### 安全控制

- **禁止命令列表**: 直接拒绝执行危险命令
- **敏感命令列表**: 弹出桌面窗口提示用户确认后执行
- **文件传输审批**: 上传/下载文件需要用户确认
- **审计日志**: 记录所有命令执行历史
- **提权执行**: 权限不足时可使用sudo提权

## 🚀 快速开始

### 1. 编译项目

```bash
dotnet build LitSSHmcp.slnx
```

### 2. 发布为独立exe

```bash
dotnet publish src/LitSSHmcp.McpServer -c Release -r win-x64 --self-contained -o publish
```

发布后的文件位于：`publish/LitSSHmcp.McpServer.exe`

### 3. 配置SSH服务器

**方式一：使用WPF管理界面**

```bash
dotnet run --project src/LitSSHmcp.App
```

打开管理界面后，点击"添加"按钮配置SSH服务器。

**方式二：手动编辑配置文件**

配置文件位置：`%APPDATA%\LitSSH\config.json`

```json
{
  "servers": [
    {
      "id": "my-server",
      "name": "我的服务器",
      "host": "192.168.1.100",
      "port": 22,
      "username": "root",
      "authType": "Password",
      "password": "your-password",
      "sudoType": "CurrentUser",
      "sudoPassword": "your-sudo-password"
    }
  ],
  "security": {
    "commandFilter": {
      "blockedCommands": ["rm -rf /", "mkfs", "dd if=/dev/zero"],
      "sensitiveCommands": ["rm ", "chmod", "reboot", "shutdown"],
      "sensitivePatterns": ["\\brm\\b", "\\bchmod\\b"]
    },
    "fileTransfer": {
      "enabled": true,
      "requireApproval": true,
      "maxFileSizeBytes": 104857600
    }
  }
}
```

### 4. 在AI客户端中配置MCP

#### Claude Desktop

编辑配置文件 `%APPDATA%\Claude\claude_desktop_config.json`：

```json
{
  "mcpServers": {
    "litssh": {
      "command": "C:\\path\\to\\publish\\LitSSHmcp.McpServer.exe"
    }
  }
}
```

#### Cursor / Windsurf

编辑 `.cursor/mcp.json`：

```json
{
  "mcpServers": {
    "litssh": {
      "command": "C:\\path\\to\\publish\\LitSSHmcp.McpServer.exe"
    }
  }
}
```

#### Cline (VS Code)

编辑 `.vscode/mcp.json`：

```json
{
  "servers": {
    "litssh": {
      "command": "C:\\path\\to\\publish\\LitSSHmcp.McpServer.exe"
    }
  }
}
```

#### CodeBuddy

通过界面配置或编辑 `~/.codebuddy/.mcp.json`：

```json
{
  "mcpServers": {
    "litssh": {
      "type": "stdio",
      "command": "C:\\path\\to\\publish\\LitSSHmcp.McpServer.exe"
    }
  }
}
```

#### 开发模式（使用dotnet run）

```json
{
  "mcpServers": {
    "litssh": {
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\LitSSHmcp\\src\\LitSSHmcp.McpServer", "--no-build"]
    }
  }
}
```

### 5. 验证配置

重启AI客户端后，输入以下内容测试：

```
请列出所有SSH服务器
```

AI会调用 `list_servers` 工具返回服务器列表。

## 📖 使用示例

### 执行命令

```
在Web服务器上执行 df -h 查看磁盘空间
```

### 提权执行

```
查看nginx进程状态（需要root权限）
```

AI会自动使用 `execute_with_sudo` 工具提权执行。

### 文件传输

```
把本地的 config.yml 上传到服务器的 /etc/nginx/ 目录
```

```
下载服务器上的 /var/log/nginx/access.log 到桌面
```

## 🔐 提权配置

### 提权方式

| 提权方式 | 说明 | 适用场景 |
|---------|------|---------|
| 不启用 | 不使用提权 | 普通用户操作 |
| 当前用户sudo | 使用SSH用户密码执行sudo | CentOS/Ubuntu/Debian常用 |
| root用户 | 切换到root用户 | 需要root密码 |
| 指定用户 | 切换到指定用户 | 需要该用户密码 |

### Linux系统sudo机制

- `sudo command` 需要输入**当前SSH用户**的密码
- 如果sudoers配置了 `NOPASSWD`，则不需要密码
- root用户执行sudo不需要密码

## 📁 项目结构

```
LitSSHmcp/
├── LitSSHmcp.slnx                    # 解决方案文件
├── README.md                         # 使用说明
├── LICENSE                           # MIT协议
├── .gitignore                        # Git忽略文件
├── config/
│   └── config.example.json           # 配置文件示例
└── src/
    ├── LitSSHmcp.Core/               # 核心业务逻辑
    │   ├── Models/                   # 数据模型
    │   └── Services/                 # SSH、安全、存储服务
    ├── LitSSHmcp.McpServer/          # MCP服务器(Stdio传输)
    │   ├── Program.cs                # 入口点
    │   └── Tools/                    # 11个MCP Tools
    ├── LitSSHmcp.App/                # WPF桌面管理界面
    │   ├── Views/                    # 窗口界面
    │   └── ViewModels/               # MVVM视图模型
    └── LitSSHmcp.Cli/                # 命令行SSH工具
        └── Program.cs                # CLI入口
```

## 🛠️ 命令行工具 (CLI)

LitSSH提供命令行SSH连接工具，可直接在终端连接服务器。

### 发布CLI工具

```bash
dotnet publish src/LitSSHmcp.Cli -c Release -r win-x64 --self-contained -o publish
```

### 使用方法

```bash
# 列出所有服务器
litssh list

# 连接到服务器(交互式)
litssh connect web-server

# 在服务器上执行单条命令
litssh run web-server "df -h"
litssh run my-server "docker ps"
```

## 🤝 贡献

欢迎贡献！请提交Issue或Pull Request。

## 📄 许可证

本项目采用 MIT 许可证 - 查看 [LICENSE](LICENSE) 文件了解详情。

## 🔗 相关链接

- [MCP协议](https://modelcontextprotocol.io/)
- [SSH.NET](https://github.com/sshnet/SSH.NET)
- [.NET 8](https://dotnet.microsoft.com/)

## 📧 联系方式

如有问题或建议，请提交Issue。