namespace LitSSHmcp.Core.Services.Storage;

/// <summary>
/// 审计写入的当前上下文（AsyncLocal）。由 MCP 的 CallTool 请求过滤器在调用工具前置入当前工具名，
/// <see cref="AuditLogService"/> 写库时若记录未显式带 Tool 则自动补上——无需各工具手动传参。
/// </summary>
public static class AuditContext
{
    private static readonly AsyncLocal<string?> Tool = new();
    private static readonly AsyncLocal<string?> UserId = new();

    public static string? CurrentTool
    {
        get => Tool.Value;
        set => Tool.Value = value;
    }

    /// <summary>
    /// 当前操作用户（Web/登录用户）；未登录桌面会话为 null（按 local 归因）。
    /// 由宿主在启动/会话上下文中注入，<b>绝不接受工具参数或前端传入</b>（防伪造，见评估报告 §10.A.5）。
    /// M1 落地：写入审计记录的 UserId 列并纳入哈希链 payload（AuditFormatVersion v6）；
    /// MCP 子进程启动时读取宿主注入的环境变量 <c>LITSSH_OWNER_USER_ID</c>，未注入则归因 "local"。
    /// </summary>
    public static string? CurrentUserId
    {
        get => UserId.Value;
        set => UserId.Value = value;
    }
}
