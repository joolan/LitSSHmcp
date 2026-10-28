using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>后台 consolidation 的手动入口与候查看工具（仅在 agent.consolidation.enabled=true 时注册）。</summary>
public static class ConsolidationTools
{
    public static IReadOnlyList<IAgentTool> Create(AgentConsolidationService consolidation)
    {
        var run = new LocalFunction(
            "ops_consolidation_run",
            "手动触发一次记忆 consolidation: 把未处理的 raw 会话片段提炼为 pending 事件/资产事实/脚本候选（需人工审核后写入），不自动应用。后台也会按 agent.consolidation 配置在成功轮后运行。",
            """{"type":"object","properties":{}}""",
            async (args, ct) => await consolidation.RunAsync(force: true, ct));

        var list = new LocalFunction(
            "ops_consolidation_list",
            "查看 consolidation 候选(pending/applied/rejected), 用于确认后台提炼了什么、哪些事实/脚本候选需要人工审核",
            """{"type":"object","properties":{"status":{"type":"string","enum":["pending","applied","rejected","conflict"],"description":"只看该状态(省略=全部)"},"limit":{"type":"integer","description":"最多返回条数, 默认20, 上限200"}},"required":[]}""",
            async (args, ct) => await consolidation.ListCandidatesAsync(
                ArgumentReader.ReadString(args, "status"),
                ArgumentReader.ReadInt(args, "limit") ?? 20, ct));

        return new IAgentTool[]
        {
            new LocalAgentTool(list, readOnly: true),
            new LocalAgentTool(run)
        };
    }
}
