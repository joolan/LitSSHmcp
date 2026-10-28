using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 事件/事故记忆工具：记录一次排障的完整证据链，供后续同类问题按资产复用。
/// 与结构化经验（ops_memory_*）分工：经验是“可复用路径”，事件是“某次发生了什么、证据与结果”。
/// </summary>
public static class IncidentTools
{
    public static IReadOnlyList<IAgentTool> Create(AgentMemoryService memory)
    {
        var write = new LocalFunction(
            "ops_incident_write",
            "记录一次运维事件/事故: mode=save 写入现象→证据→根因→处置→结果(成功/失败均记录), mode=supersede 把旧事件标记为过时并指向新事件; 事件按资产关联, 供后续同类问题检索",
            """{"type":"object","properties":{"mode":{"type":"string","enum":["save","supersede"],"description":"save=新增事件, supersede=旧事件过时"},"title":{"type":"string","description":"save: 一句话事件标题"},"symptom":{"type":"string","description":"save: 现象/告警/用户描述"},"assetIds":{"type":"string","description":"save: 关联资产ID, 逗号分隔, 如 ssh:web01,ds:mysql01"},"rootCause":{"type":"string","description":"save: 根因(不确定可写假设)"},"actions":{"type":"string","description":"save: 实际执行的关键操作与结果"},"outcome":{"type":"string","description":"save: 最终结果(已恢复/未解决/回滚等)"},"evidence":{"type":"string","description":"save: 证据引用(快照ID/工具调用ID/日志路径/关键输出摘要)"},"confidence":{"type":"number","description":"save: 置信度 0~1, 默认0.5"},"status":{"type":"string","enum":["open","resolved"],"description":"save: open=进行中, resolved=已解决"},"oldId":{"type":"integer","description":"supersede: 旧事件ID"},"newId":{"type":"integer","description":"supersede: 新事件ID"}},"required":["mode"]}""",
            async (args, ct) =>
            {
                var mode = (ArgumentReader.ReadString(args, "mode") ?? "save").Trim().ToLowerInvariant();
                return mode switch
                {
                    "save" => await memory.SaveIncidentAsync(
                        ArgumentReader.ReadString(args, "title") ?? string.Empty,
                        ArgumentReader.ReadString(args, "symptom") ?? string.Empty,
                        ArgumentReader.ReadString(args, "assetIds") ?? string.Empty,
                        ArgumentReader.ReadString(args, "rootCause") ?? string.Empty,
                        ArgumentReader.ReadString(args, "actions") ?? string.Empty,
                        ArgumentReader.ReadString(args, "outcome") ?? string.Empty,
                        ArgumentReader.ReadString(args, "evidence") ?? string.Empty,
                        ReadDouble(args, "confidence") ?? 0.5,
                        ArgumentReader.ReadString(args, "status"), ct),
                    "supersede" => await memory.SupersedeIncidentAsync(
                        ReadLong(args, "oldId") ?? 0,
                        ReadLong(args, "newId") ?? 0, ct),
                    _ => "错误: mode 仅支持 save/supersede"
                };
            });

        var list = new LocalFunction(
            "ops_incident_list",
            "列出/检索事件记录: 可按资产ID或状态过滤, 返回现象/根因/处置/结果/证据; 排查前先查该资产历史事件",
            """{"type":"object","properties":{"assetId":{"type":"string","description":"只看涉及该资产的事件, 如 ssh:web01"},"status":{"type":"string","enum":["open","resolved","superseded"],"description":"只看该状态(省略=全部)"},"limit":{"type":"integer","description":"最多返回条数, 默认20, 上限200"}},"required":[]}""",
            async (args, ct) => await memory.ListIncidentsAsync(
                ArgumentReader.ReadString(args, "assetId"),
                ArgumentReader.ReadString(args, "status"),
                ArgumentReader.ReadInt(args, "limit") ?? 20, ct));

        return new IAgentTool[]
        {
            new LocalAgentTool(list, readOnly: true),
            new LocalAgentTool(write)
        };
    }

    private static long? ReadLong(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } je
                when je.TryGetInt64(out var n) => n,
            System.Text.Json.JsonElement je => long.TryParse(je.ToString(), out var p) ? p : null,
            string s => long.TryParse(s, out var q) ? q : null,
            _ => null
        };
    }

    private static double? ReadDouble(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } je
                when je.TryGetDouble(out var n) => n,
            System.Text.Json.JsonElement je => double.TryParse(je.ToString(), out var p) ? p : null,
            string s => double.TryParse(s, out var q) ? q : null,
            _ => null
        };
    }
}
