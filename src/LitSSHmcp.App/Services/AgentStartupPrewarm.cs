using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 启动后台预热：主窗口首帧 + 调度器启动后，提前连接最可能打开的 AI 会话的内置 MCP 宿主。
/// 用户真正打开 AI 助手时直接命中宿主池，省掉子进程 spawn + initialize + tools/list（约 1.5s）。
/// 失败/未启用/未配置 AI 时静默跳过；空闲宿主由池按 TTL 回收。
/// </summary>
internal static class AgentStartupPrewarm
{
    public static async Task RunAsync()
    {
        try
        {
            // 让主窗口首帧、调度器和配置读取先完成，避免和启动争抢 I/O。
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

            var configService = AppServiceFactory.CreateConfigService();
            var config = await configService.LoadConfigAsync().ConfigureAwait(false);
            var agent = config.Agent ?? new AgentConfig();
            if (!agent.Enabled)
                return;

            var builtin = AgentMcpServerConfig.Normalize(agent.McpServers).FirstOrDefault(s => s.Builtin);
            if (builtin is not { Enabled: true })
                return;
            if (!AgentProviderGroupConfig.Flatten(agent.Providers).Any(p => p.Enabled))
                return;

            // 资产图谱提示词首开约 500ms：与 MCP 预热并行，在后台按 config 版本写入缓存。
            var configPath = configService.GetConfigPath();
            _ = Task.Run(() => AssetGraphPromptCache.Prewarm(configPath, config));

            var launch = AgentPaths.ResolveLaunch(agent.McpServerPath);
            if (launch is null)
                return;

            var store = AppServiceFactory.CreateAgentContextStore();
            await store.InitializeAsync().ConfigureAwait(false);
            var sessions = await store.ListSessionsAsync().ConfigureAwait(false);
            var first = sessions.FirstOrDefault();
            if (first is null)
                return;

            // AgentViewModel.InitializeAsync 默认打开列表第一条；用同一个会话的归属/工作目录构造池键。
            var workspace = await store.GetSessionWorkspaceDirAsync(first.Id).ConfigureAwait(false);
            var owner = await store.GetSessionOwnerUserIdAsync(first.Id).ConfigureAwait(false);
            var key = McpBuiltinHostPool.CreateKey(launch, owner, workspace,
                ownerUsername: null, ownerRole: null, configGeneration: 0);
            McpBuiltinHostPool.Prewarm(key, launch);
        }
        catch
        {
            // 预热是纯优化路径，任何失败都不影响 App
        }
    }
}
