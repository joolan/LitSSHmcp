using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

public partial class AgentViewModel
{
    private AppConfig _fullConfig = new();

    private string RelatedWorkspaceRoot => string.IsNullOrWhiteSpace(_sessionWorkspaceDir)
        ? WorkspaceTools.ResolveDir(_agentConfig.WorkspaceDir)
        : _sessionWorkspaceDir;

    private string RelatedSharedRoot => WorkspaceTools.ResolveDir(_agentConfig.WorkspaceDir);

    private void CollectRelatedTargets(AgentTurn turn, AgentStep step)
    {
        if (step.IsInfo)
            return;

        var targets = AgentRelatedTargetExtractor.Extract(
            step.Tool,
            step.ArgsJson,
            _fullConfig,
            RelatedWorkspaceRoot,
            RelatedSharedRoot,
            !string.IsNullOrWhiteSpace(_sessionWorkspaceDir),
            step.Success);

        foreach (var target in targets)
            turn.UpsertRelated(target);
    }

    private void RebuildRelatedTargets(AgentTurn turn)
    {
        turn.ClearRelatedTargets();
        foreach (var step in turn.Steps)
            CollectRelatedTargets(turn, step);
    }
}
