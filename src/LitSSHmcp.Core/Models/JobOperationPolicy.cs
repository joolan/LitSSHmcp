namespace LitSSHmcp.Core.Models;

/// <summary>Job operation 的跨进程安全元数据（Agent 侧 profile/只读判定用）。</summary>
public sealed record JobOperationArgument(string Name, string Type, bool Required, string Description);

public sealed record JobOperationPolicy(
    string Operation,
    bool ReadOnly,
    bool RequiresWriteLock = false,
    string Description = "",
    string ArgumentsHint = "",
    string[]? RequiredGroups = null,
    JobOperationArgument[]? ArgumentsSchema = null);

/// <summary>
/// 已知 Job operation 的单一目录：安全属性、描述、参数提示与所需工具分组。
/// Agent 系统提示、mcp_usage_guide 与 docs/TOOLS.md 的一致性测试都从这里生成/校验。
/// 新增 operation 时须同步登记，否则 Agent 侧 fail-closed。
/// </summary>
public static class JobOperationPolicies
{
    private static readonly IReadOnlyDictionary<string, JobOperationPolicy> Policies =
        new Dictionary<string, JobOperationPolicy>(StringComparer.Ordinal)
        {
            ["topology_discover"] = new("topology_discover", ReadOnly: true,
                Description: "多机全量拓扑扫描，发现服务器/进程/端口/数据库证据",
                ArgumentsHint: "serverIds?, searchPaths?",
                RequiredGroups: new[] { ToolGroups.Topology },
                ArgumentsSchema: new[]
                {
                    new JobOperationArgument("serverIds", "string", false, "逗号分隔的服务器 ID；省略=全部"),
                    new JobOperationArgument("searchPaths", "string", false, "空格分隔的额外搜索路径")
                }),
            ["ssh_snapshot_refresh"] = new("ssh_snapshot_refresh", ReadOnly: true,
                Description: "单台整机快照重采，逐采集器上报进度",
                ArgumentsHint: "serverId, force?, profile?/sections?",
                RequiredGroups: new[] { ToolGroups.Ssh },
                ArgumentsSchema: new[]
                {
                    new JobOperationArgument("serverId", "string", true, "服务器 ID"),
                    new JobOperationArgument("force", "boolean", false, "忽略最短刷新间隔，默认 false"),
                    new JobOperationArgument("profile", "string", false, "quick 或省略"),
                    new JobOperationArgument("sections", "string", false, "逗号分隔的采集域，优先于 profile")
                }),
            ["fleet_inventory"] = new("fleet_inventory", ReadOnly: true,
                Description: "多机在线/端口/Docker/Jenkins 标准巡检",
                ArgumentsHint: "serverIds, refresh?, maxAgeMinutes?",
                RequiredGroups: new[] { ToolGroups.Ssh },
                ArgumentsSchema: new[]
                {
                    new JobOperationArgument("serverIds", "string", true, "逗号分隔的服务器 ID"),
                    new JobOperationArgument("refresh", "boolean", false, "是否先刷新快照，默认 false"),
                    new JobOperationArgument("maxAgeMinutes", "integer", false, "快照最大年龄，默认 60")
                }),
            ["fleet_snapshot_refresh"] = new("fleet_snapshot_refresh", ReadOnly: true,
                Description: "多机快照批量刷新",
                ArgumentsHint: "serverIds, force?, profile?/sections?",
                RequiredGroups: new[] { ToolGroups.Ssh },
                ArgumentsSchema: new[]
                {
                    new JobOperationArgument("serverIds", "string", true, "逗号分隔的服务器 ID"),
                    new JobOperationArgument("force", "boolean", false, "忽略最短刷新间隔，默认 false"),
                    new JobOperationArgument("profile", "string", false, "quick 或省略"),
                    new JobOperationArgument("sections", "string", false, "逗号分隔的采集域，优先于 profile")
                })
        };

    public static IReadOnlyList<JobOperationPolicy> All { get; } =
        Policies.Values.OrderBy(p => p.Operation, StringComparer.Ordinal).ToArray();

    /// <summary>按当前启用分组过滤 operation；null=全部。</summary>
    public static IReadOnlyList<JobOperationPolicy> ForGroups(IReadOnlyCollection<string>? groups)
    {
        if (groups is null)
            return All;
        return All.Where(p => p.RequiredGroups is null || p.RequiredGroups.All(g =>
            groups.Any(e => string.Equals(e, g, StringComparison.OrdinalIgnoreCase)))).ToArray();
    }

    public static JobOperationPolicy? Find(string? operation) =>
        operation is not null && Policies.TryGetValue(operation, out var policy) ? policy : null;

    public static bool IsAllowed(string? operation, AgentToolProfile profile, bool readOnly)
    {
        var policy = Find(operation);
        if (policy is null)
            return false;
        if (readOnly && !policy.ReadOnly)
            return false;
        return profile switch
        {
            AgentToolProfile.Diagnose or AgentToolProfile.Assets or AgentToolProfile.Chat => policy.ReadOnly,
            _ => true
        };
    }
}
