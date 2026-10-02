namespace LitSSHmcp.Core.Services.Storage;

/// <summary>
/// 审计写入的当前上下文（AsyncLocal）。由 MCP 的 CallTool 请求过滤器在调用工具前置入当前工具名，
/// <see cref="AuditLogService"/> 写库时若记录未显式带 Tool 则自动补上——无需各工具手动传参。
/// </summary>
public static class AuditContext
{
    private static readonly AsyncLocal<string?> Tool = new();

    public static string? CurrentTool
    {
        get => Tool.Value;
        set => Tool.Value = value;
    }
}
