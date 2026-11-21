using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

public partial class AgentViewModel
{
    /// <summary>
    /// 会话选定后后台预热内置 MCP 宿主。与读取会话转录并行执行；正式装配时若键一致，
    /// 直接从池中拿到已连接的子进程，省掉 spawn + initialize + tools/list（实测约 1.5s）。
    /// 预热失败只影响速度，不影响正式连接。
    /// </summary>
    private void PrewarmBuiltinMcpHost()
    {
        try
        {
            var launch = AgentPaths.ResolveLaunch(_agentConfig.McpServerPath);
            if (launch is null)
                return;

            var servers = McpSubsetPolicy.EffectiveServers(
                AgentMcpServerConfig.Normalize(_agentConfig.McpServers), _sessionMcpSubset);
            if (servers.FirstOrDefault(s => s.Builtin) is not { Enabled: true })
                return;

            var key = McpBuiltinHostPool.CreateKey(
                launch,
                _sessionOwnerUserId,
                _sessionWorkspaceDir,
                ownerUsername: null,
                ownerRole: null,
                configGeneration: _poolVersion);
            McpBuiltinHostPool.Prewarm(key, launch);
        }
        catch
        {
            // 优化路径：失败不阻断正式连接
        }
    }
}
