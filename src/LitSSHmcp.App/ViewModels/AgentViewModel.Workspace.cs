using LitSSHmcp.Agent;

namespace LitSSHmcp.App.ViewModels;

public partial class AgentViewModel
{
    /// <summary>共享工作区根目录（左侧「打开工作目录」；不随当前选中会话切换）。</summary>
    public string SharedWorkspaceRoot => WorkspaceTools.ResolveDir(_agentConfig.WorkspaceDir);

    /// <summary>读取指定会话的独立工作目录；未分配（空会话/旧会话）返回 null。</summary>
    public async Task<string?> GetSessionWorkspaceDirAsync(AgentSessionRow session)
    {
        if (session.Id <= 0)
            return null;
        try
        {
            var dir = await _store.GetSessionWorkspaceDirAsync(session.Id);
            return string.IsNullOrWhiteSpace(dir) ? null : dir;
        }
        catch
        {
            return null;
        }
    }
}
