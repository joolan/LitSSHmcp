namespace LitSSHmcp.Agent;

/// <summary>注入系统提示的外部 MCP 摘要（非持久化）。</summary>
/// <param name="Name">服务器显示名。</param>
/// <param name="ToolCount">可用工具数（连接失败为 0）。</param>
/// <param name="Renamed">因与既有工具重名而加了服务器前缀的映射说明（如 <c>`a_ssh_exec` → `ssh_exec`</c>）。</param>
/// <param name="Error">连接失败原因（成功为 null）。</param>
public sealed record ExternalMcpServerInfo(string Name, int ToolCount, IReadOnlyList<string> Renamed, string? Error);
