using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 统一知识写入入口：诊断/变更报告 + 可复用记忆，共用一个本地工具，减少模型工具面。
/// 底层仍复用 DiagnosisTools（自动写 incident）与 MemoryTools（kind/key 记忆），不复制业务逻辑。
/// </summary>
public static class OpsRecordTools
{
    public const string ToolName = "ops_record";

    public static IAgentTool Create(IAgentTool diagnosisTool, IAgentTool? memoryWriteTool)
    {
        var function = new LocalFunction(
            ToolName,
            "记录运维知识：action=diagnosis|change 生成结构化诊断/变更报告并写 incident；action=memory_save|memory_update|memory_delete 写可复用经验。单次事件用 diagnosis，跨事件复用的结论才用 memory_*，不要重复写两处。change 必须填 verification 和 rollback。",
            """
            {"type":"object","properties":{
              "action":{"type":"string","enum":["diagnosis","change","memory_save","memory_update","memory_delete"],"description":"diagnosis/change=诊断或变更报告, memory_*=可复用经验"},
              "title":{"type":"string","description":"diagnosis/change: 一句话结论"},
              "assetIds":{"type":"string","description":"资产 ID, 逗号分隔, 如 ssh:web01,ds:mysql01"},
              "symptom":{"type":"string","description":"diagnosis/change: 现象/告警/用户描述"},
              "evidence":{"type":"string","description":"diagnosis/change: 证据(命令/日志路径/关键输出)"},
              "rootCause":{"type":"string","description":"diagnosis/change: 根因(不确定可留空)"},
              "actions":{"type":"string","description":"diagnosis/change: 实际执行的关键处置"},
              "verification":{"type":"string","description":"change: 验证方式与结果"},
              "impact":{"type":"string","description":"diagnosis/change: 影响面"},
              "rollback":{"type":"string","description":"change: 回滚方式"},
              "outcome":{"type":"string","description":"diagnosis/change: 最终结果"},
              "status":{"type":"string","enum":["open","resolved"],"description":"diagnosis/change: 事件状态"},
              "confidence":{"type":"number","description":"diagnosis/change: 置信度 0~1"},
              "kind":{"type":"string","enum":["task","script","asset"],"description":"memory_*: 记忆类型"},
              "key":{"type":"string","description":"memory_*: 记忆唯一键, 如 路径:ssh:web01"},
              "content":{"type":"string","description":"memory_save/update: 记忆内容"},
              "tags":{"type":"string","description":"memory_*: 标签, 逗号分隔"},
              "memoryStatus":{"type":"string","enum":["active","superseded"],"description":"memory_update: active/superseded"}
            },"required":["action"]}
            """,
            async (args, ct) =>
            {
                var action = (ArgumentReader.ReadString(args, "action") ?? string.Empty).Trim().ToLowerInvariant();
                var mapped = new Dictionary<string, object?>(args, StringComparer.Ordinal);
                switch (action)
                {
                    case "diagnosis":
                    case "change":
                        mapped["mode"] = action == "change" ? "change" : "diagnose";
                        return await diagnosisTool.InvokeAsync(mapped, ct);
                    case "memory_save":
                    case "memory_update":
                    case "memory_delete":
                        if (memoryWriteTool is null)
                            return "(记忆未启用：需在 AI 助手设置中开启长期记忆/Embeddings)";
                        mapped["action"] = action["memory_".Length..];
                        if (mapped.TryGetValue("memoryStatus", out var memoryStatus))
                            mapped["status"] = memoryStatus;
                        return await memoryWriteTool.InvokeAsync(mapped, ct);
                    default:
                        return "错误: action 仅支持 diagnosis/change/memory_save/memory_update/memory_delete";
                }
            });

        return new LocalAgentTool(function, readOnly: memoryWriteTool is null && diagnosisTool.ReadOnly);
    }
}
