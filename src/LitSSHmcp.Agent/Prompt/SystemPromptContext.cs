namespace LitSSHmcp.Agent;

/// <summary>提示词组装上下文：把原先 20+ 个 Build 参数收敛为一个稳定对象，便于按模式/能力包拆分。</summary>
public sealed class SystemPromptContext
{
    public string? ServerInstructions { get; init; }
    public string? Skills { get; init; }
    public string? UserPrompt { get; init; }
    public bool WorkspaceTools { get; init; } = true;
    public IReadOnlyList<string>? SkillFiles { get; init; }
    public bool SpillTools { get; init; }
    public bool SubAgent { get; init; }
    public string? ResponseStyle { get; init; }
    public IReadOnlyCollection<string>? AvailableGroups { get; init; }
    public string? AgentName { get; init; }
    public string? AgentPrompt { get; init; }
    public string? AgentStyle { get; init; }
    public string? SkillsRoot { get; init; }
    public IReadOnlyList<ExternalMcpServerInfo>? ExternalMcp { get; init; }
    public bool BuiltinMcpDisabled { get; init; }
    public bool MemoryTools { get; init; }
    public bool ScriptTools { get; init; }
    public string? AssetGraph { get; init; }
    public string? SessionWorkspaceDir { get; init; }
    public string? SharedWorkspaceDir { get; init; }
    public bool WorkLog { get; init; }
    public string? WorkLogDir { get; init; }
    public string? WorkLogPath { get; init; }
    public string? WorkLogRoot { get; init; }
    public bool FleetTools { get; init; }
    public bool FleetChangeTools { get; init; }
    public string? ProfileName { get; init; }
    public bool DiagnosisTools { get; init; }
    public bool InsightTools { get; init; }
}
