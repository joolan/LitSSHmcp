using System.Text;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 结构化诊断/变更报告工具：把一次排障或变更整理成固定小节（现象/证据/根因/处置/验证/影响面/回滚），
/// 有记忆时同时写入事件记录；无记忆/只读模式下只生成报告（只读工具）。
/// </summary>
public static class DiagnosisTools
{
    public const string ToolName = "ops_diagnosis_write";

    public static IAgentTool Create(AgentMemoryService? memory)
    {
        var function = new LocalFunction(
            ToolName,
            "生成结构化诊断/变更报告（最终回答直接使用其返回文本）；有记忆时同时写入事件记录。诊断/变更任务结束前必须调用。mode=change 必须填写 verification 和 rollback，没有验证不得声称成功。",
            """
            {"type":"object","properties":{
              "mode":{"type":"string","enum":["diagnose","change"],"description":"diagnose=诊断/排障, change=变更/发布"},
              "title":{"type":"string","description":"一句话结论/标题"},
              "assetIds":{"type":"string","description":"关联资产ID, 逗号分隔"},
              "symptom":{"type":"string","description":"现象/告警/用户描述"},
              "evidence":{"type":"string","description":"证据: 工具/快照ID/日志路径/关键输出"},
              "rootCause":{"type":"string","description":"根因(不确定可写假设)"},
              "actions":{"type":"string","description":"实际执行的关键操作"},
              "verification":{"type":"string","description":"验证方式与结果"},
              "impact":{"type":"string","description":"影响面/受影响资产"},
              "rollback":{"type":"string","description":"回滚方式(change 必填)"},
              "outcome":{"type":"string","description":"最终结果: 已恢复/未解决/回滚等"},
              "status":{"type":"string","enum":["open","resolved"],"description":"事件状态, 默认 resolved"},
              "confidence":{"type":"number","description":"置信度 0~1, 默认0.5"}
            },"required":["mode","title"]}
            """,
            async (args, ct) =>
            {
                var mode = (ArgumentReader.ReadString(args, "mode") ?? "diagnose").Trim().ToLowerInvariant();
                if (mode is not ("diagnose" or "change"))
                    return "错误: mode 仅支持 diagnose/change";

                var title = (ArgumentReader.ReadString(args, "title") ?? string.Empty).Trim();
                if (title.Length == 0)
                    return "错误: title 为必填";
                if (title.Length > 200)
                    return "错误: title 过长(≤200 字符)";

                var assetIds = (ArgumentReader.ReadString(args, "assetIds") ?? string.Empty).Trim();
                var symptom = (ArgumentReader.ReadString(args, "symptom") ?? string.Empty).Trim();
                var evidence = (ArgumentReader.ReadString(args, "evidence") ?? string.Empty).Trim();
                var rootCause = (ArgumentReader.ReadString(args, "rootCause") ?? string.Empty).Trim();
                var actions = (ArgumentReader.ReadString(args, "actions") ?? string.Empty).Trim();
                var verification = (ArgumentReader.ReadString(args, "verification") ?? string.Empty).Trim();
                var impact = (ArgumentReader.ReadString(args, "impact") ?? string.Empty).Trim();
                var rollback = (ArgumentReader.ReadString(args, "rollback") ?? string.Empty).Trim();
                var outcome = (ArgumentReader.ReadString(args, "outcome") ?? string.Empty).Trim();
                var status = (ArgumentReader.ReadString(args, "status") ?? "resolved").Trim().ToLowerInvariant();
                if (status is not ("open" or "resolved"))
                    status = "resolved";
                var confidence = ReadDouble(args, "confidence") ?? 0.5;
                if (!double.IsFinite(confidence))
                    confidence = 0.5;

                if (mode == "change")
                {
                    if (string.IsNullOrWhiteSpace(verification))
                        return "错误: 变更报告必须填写 verification（变更后验证方式与结果）";
                    if (string.IsNullOrWhiteSpace(rollback))
                        return "错误: 变更报告必须填写 rollback（回滚方式）";
                }

                var report = BuildReport(mode, title, symptom, evidence, rootCause, actions,
                    verification, impact, rollback, outcome, confidence);
                if (memory is null)
                    return report;

                var incidentActions = actions;
                if (!string.IsNullOrWhiteSpace(verification))
                    incidentActions += "\n验证: " + verification;
                if (!string.IsNullOrWhiteSpace(impact))
                    incidentActions += "\n影响面: " + impact;
                if (!string.IsNullOrWhiteSpace(rollback))
                    incidentActions += "\n回滚: " + rollback;
                var saved = await memory.SaveIncidentAsync(title, symptom, assetIds, rootCause,
                    incidentActions, outcome, evidence, Math.Clamp(confidence, 0, 1), status, ct);
                return report + "\n\n> " + saved;
            });

        // 无记忆/只读模式：只生成报告，不落库，因此标记为只读工具。
        return new LocalAgentTool(function, readOnly: memory is null);
    }

    private static string BuildReport(string mode, string title, string symptom, string evidence,
        string rootCause, string actions, string verification, string impact, string rollback,
        string outcome, double confidence)
    {
        var sb = new StringBuilder();
        sb.AppendLine(mode == "change" ? "## 变更报告" : "## 诊断报告");
        sb.AppendLine($"**结论**: {title}");
        if (!string.IsNullOrWhiteSpace(symptom)) sb.AppendLine($"- **现象**: {Truncate(symptom, 2000)}");
        if (!string.IsNullOrWhiteSpace(evidence)) sb.AppendLine($"- **证据**: {Truncate(evidence, 3000)}");
        if (!string.IsNullOrWhiteSpace(rootCause)) sb.AppendLine($"- **根因**: {Truncate(rootCause, 2000)}");
        if (!string.IsNullOrWhiteSpace(actions)) sb.AppendLine($"- **处置**: {Truncate(actions, 3000)}");
        if (!string.IsNullOrWhiteSpace(verification)) sb.AppendLine($"- **验证**: {Truncate(verification, 2000)}");
        if (!string.IsNullOrWhiteSpace(impact)) sb.AppendLine($"- **影响面**: {Truncate(impact, 2000)}");
        if (!string.IsNullOrWhiteSpace(rollback)) sb.AppendLine($"- **回滚**: {Truncate(rollback, 2000)}");
        if (!string.IsNullOrWhiteSpace(outcome)) sb.AppendLine($"- **结果**: {Truncate(outcome, 1000)}");
        sb.Append($"- **置信度**: {Math.Clamp(confidence, 0, 1):0.##}");
        return sb.ToString().Trim();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

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
                when je.TryGetDouble(out var number) => number,
            System.Text.Json.JsonElement je => double.TryParse(je.ToString(), out var parsed) ? parsed : null,
            string s => double.TryParse(s, out var parsed) ? parsed : null,
            _ => null
        };
    }
}
