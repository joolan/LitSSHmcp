# MCP 客户端配置

先发布（见 [快速开始](../README.md#快速开始)）得到 `LitSSHmcp.McpServer.exe`，再按客户端填入配置。

<details>
<summary><b>Claude Desktop</b>（编辑 <code>%APPDATA%\Claude\claude_desktop_config.json</code>）</summary>

```json
{
  "mcpServers": {
    "litssh": {
      "command": "C:\\path\\to\\publish\\LitSSHmcp.McpServer.exe"
    }
  }
}
```
</details>

<details>
<summary><b>Cursor / Windsurf</b>（编辑 <code>.cursor/mcp.json</code>）</summary>

```json
{
  "mcpServers": {
    "litssh": {
      "command": "C:\\path\\to\\publish\\LitSSHmcp.McpServer.exe"
    }
  }
}
```
</details>

<details>
<summary><b>Cline (VS Code)</b>（编辑 <code>.vscode/mcp.json</code>）</summary>

```json
{
  "servers": {
    "litssh": {
      "command": "C:\\path\\to\\publish\\LitSSHmcp.McpServer.exe"
    }
  }
}
```
</details>

<details>
<summary><b>CodeBuddy</b>（编辑 <code>~/.codebuddy/.mcp.json</code>）</summary>

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
</details>

<details>
<summary><b>开发模式（使用 dotnet run）</b></summary>

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
</details>
