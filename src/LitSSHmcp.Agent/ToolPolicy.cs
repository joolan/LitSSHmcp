namespace LitSSHmcp.Agent;

/// <summary>
/// 工具级审批策略（会话级，Agent 层）：控制 Agent 是否调用某工具。
/// 注意：**拒绝**在调用前拦截；**允许**仅表示 Agent 不再额外限制，**不绕过 MCP 服务端**的桌面审批（安全边界仍在服务端）。
/// </summary>
public enum AgentToolPolicy
{
    /// <summary>默认：正常调用，敏感操作由 MCP 服务端审批。</summary>
    Default,

    /// <summary>允许：Agent 不额外限制（仍受服务端审批）。</summary>
    Allow,

    /// <summary>拒绝：Agent 调用前拦截，不执行。</summary>
    Deny
}

public static class AgentToolPolicyParser
{
    public static AgentToolPolicy Parse(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "allow" or "approve" or "approved" => AgentToolPolicy.Allow,
        "deny" or "reject" or "rejected" => AgentToolPolicy.Deny,
        _ => AgentToolPolicy.Default
    };

    public static string ToName(AgentToolPolicy policy) => policy switch
    {
        AgentToolPolicy.Allow => "allow",
        AgentToolPolicy.Deny => "deny",
        _ => "default"
    };
}

/// <summary>工具目录项（供「工具审批策略」窗口展示）。</summary>
public sealed record ToolPolicyItem(string Name, string Group, bool ReadOnly, bool Destructive);
