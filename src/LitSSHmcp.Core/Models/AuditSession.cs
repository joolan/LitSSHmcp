namespace LitSSHmcp.Core.Models;

/// <summary>
/// 一次 MCP 会话的元信息（每次启动 MCP 服务生成一个 SessionId）。
/// 客户端名称/版本在 initialize 握手后由服务端从请求上下文获取并回填。
/// </summary>
public class AuditSession
{
    public string SessionId { get; set; } = string.Empty;
    public string? ClientName { get; set; }
    public string? ClientVersion { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
