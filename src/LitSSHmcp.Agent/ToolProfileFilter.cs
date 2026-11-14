using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Agent;

/// <summary>
/// 会话工具 profile 裁剪：在 <c>tools.enabledGroups</c> 与只读开关之外，再按场景收窄工具面。
/// 远程 MCP 工具按分组/只读判定；本地工具按名称白名单判定（本地记忆写入在诊断模式仍可用）。
/// </summary>
public static class ToolProfileFilter
{
    private static readonly HashSet<string> AssetGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        ToolGroups.Ssh, ToolGroups.FileTransfer, ToolGroups.Topology, ToolGroups.App,
        ToolGroups.Datasource, ToolGroups.Docker, ToolGroups.Service,
        ToolGroups.Mysql, ToolGroups.Postgres, ToolGroups.Redis, ToolGroups.Guide
    };

    /// <summary>
    /// 非只读但"只做探测、不改目标系统"的工具：可用于诊断/资产等收窄 profile。
    /// 它们会写本地快照/拓扑缓存，因此 MCP ReadOnlyHint 仍为 false（安全只读模式仍会排除）；
    /// 这里只放宽会话 profile 的工具面，不改变服务端授权/审批/审计。
    /// </summary>
    private static readonly HashSet<string> ProfileSafeNonMutating = new(StringComparer.OrdinalIgnoreCase)
    {
        "ssh_snapshot_refresh",
        "topology_discover",
        // job_start 本身不直接操作目标；当前注册的 operation（topology/snapshot/fleet_inventory/
        // fleet_snapshot_refresh）均为只读探测。将来接入写型 operation 时必须改为 operation 级判定。
        "job_start"
    };

    /// <summary>profile/计划模式下的“安全只读”判定：MCP ReadOnly 或非变更探测白名单。</summary>
    public static bool IsProfileReadOnly(IAgentTool tool) =>
        tool.ReadOnly || ProfileSafeNonMutating.Contains(tool.Name);

    /// <summary>
    /// 是否为“只写本地缓存、远端仅执行固定只读命令”的安全探测工具
    /// （如 ssh_snapshot_refresh / topology_discover）。只读模式允许它们，
    /// 但只应对内置 MCP 放行，避免外部 MCP 用同名工具绕过只读限制。
    /// </summary>
    public static bool IsSafeLocalCacheProbe(string name) =>
        ProfileSafeNonMutating.Contains(name);

    public static bool AllowsRemote(IAgentTool tool, AgentToolProfile profile)
    {
        if (profile is AgentToolProfile.Auto or AgentToolProfile.Change or AgentToolProfile.Ops)
            return true;

        return profile switch
        {
            AgentToolProfile.Diagnose => IsProfileReadOnly(tool),
            AgentToolProfile.Assets => IsProfileReadOnly(tool) &&
                                       (tool.ToolGroup is null || AssetGroups.Contains(tool.ToolGroup)),
            AgentToolProfile.Chat => IsProfileReadOnly(tool) && string.Equals(tool.ToolGroup, ToolGroups.Guide,
                StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    public static bool AllowsLocal(string name, AgentToolProfile profile) =>
        LocalToolCatalog.AllowsLocal(name, profile);

    public static string? Display(AgentToolProfile profile) => profile switch
    {
        AgentToolProfile.Diagnose => "诊断/巡检",
        AgentToolProfile.Change => "变更/发布",
        AgentToolProfile.Assets => "资产治理",
        AgentToolProfile.Chat => "问答",
        AgentToolProfile.Ops => "运维专注",
        _ => null
    };
}
