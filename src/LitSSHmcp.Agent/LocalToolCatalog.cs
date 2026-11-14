using LitSSHmcp.Agent.Scheduling;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Agent;

/// <summary>
/// 本地工具面的单一目录：模型隐藏策略与各会话 profile 的白名单。
/// 新增本地工具时优先在这里登记；ToolSurfaceConsistencyTests 负责守门。
/// </summary>
public static class LocalToolCatalog
{
    /// <summary>保留在宿主工具集中、但不暴露给模型的本地工具。</summary>
    public static readonly IReadOnlySet<string> HiddenFromModel = new HashSet<string>(StringComparer.Ordinal)
    {
        FleetTargetTools.ToolName,
        AiTaskTools.CreateTool, AiTaskTools.ListTool, AiTaskTools.SetEnabledTool,
        AiTaskTools.DeleteTool, AiTaskTools.RunNowTool, AiTaskTools.RunsTool,
        "ops_incident_write", "ops_incident_list",
        DiagnosisTools.ToolName, "ops_memory_write"
    };

    public static readonly IReadOnlySet<string> DiagnoseVisible = new HashSet<string>(StringComparer.Ordinal)
    {
        "ops_doc_read", "ops_memory_list", "ops_memory_write", "ops_record",
        "ops_incident_list", "ops_incident_write", "ops_diagnosis_write", "ops_insight",
        "skill", "spill", "update_plan", "run_subagent", "fleet_query",
        "ai_task_list", "ai_task_runs"
    };

    public static readonly IReadOnlySet<string> AssetsVisible = new HashSet<string>(StringComparer.Ordinal)
    {
        "ops_doc_read", "ops_doc_write", "ops_memory_list", "ops_memory_write", "ops_record",
        "ops_incident_list", "ops_incident_write", "ops_diagnosis_write", "ops_insight",
        "skill", "spill", "update_plan", "run_subagent", "fleet_query", "ops_script_register",
        "ai_task_list", "ai_task_runs"
    };

    public static readonly IReadOnlySet<string> ChatVisible = new HashSet<string>(StringComparer.Ordinal)
    {
        "ops_doc_read", "skill", "spill", "ops_memory_list", "ops_insight"
    };

    /// <summary>运维专注 profile：巡检/诊断/变更/多机编排/计划/记忆，不含管理型 UI 工具。</summary>
    public static readonly IReadOnlySet<string> OpsVisible = new HashSet<string>(StringComparer.Ordinal)
    {
        "skill", "spill", "update_plan", "run_subagent",
        "fleet_query", "fleet_change",
        "ops_doc_read", "ops_doc_write", "ops_script_register",
        "ops_memory_list", "ops_memory_write", "ops_record",
        "ops_diagnosis_write", "ops_insight",
        "local_shell", "local_shell_open", "local_shell_manage", "local_shell_stop"
    };

    public static bool IsHiddenFromModel(string name) => HiddenFromModel.Contains(name);

    public static bool AllowsLocal(string name, AgentToolProfile profile) => profile switch
    {
        AgentToolProfile.Auto or AgentToolProfile.Change => true,
        AgentToolProfile.Diagnose => DiagnoseVisible.Contains(name),
        AgentToolProfile.Assets => AssetsVisible.Contains(name),
        AgentToolProfile.Chat => ChatVisible.Contains(name),
        AgentToolProfile.Ops => OpsVisible.Contains(name),
        _ => true
    };
}
