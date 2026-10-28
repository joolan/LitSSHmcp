using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 统一只读洞察入口：把时间线与资产画像合并为一个 action 工具，减少本地工具数量。
/// 两个底层工具均为只读，因此合并后仍保持 ReadOnly 注解。
/// </summary>
public static class InsightTools
{
    public const string ToolName = "ops_insight";

    public static IAgentTool Create(IAgentTool timelineTool, IAgentTool assetProfileTool)
    {
        var function = new LocalFunction(
            ToolName,
            "运维洞察：action=timeline 查事件/会话时间线（可传 incidentId/sessionId/limit）；action=asset_profile 查资产画像/影响面/历史事件/脚本经验（必须传 assetId，limit 可选）。",
            """
            {"type":"object","properties":{
              "action":{"type":"string","enum":["timeline","asset_profile"],"description":"timeline=事件/会话时间线, asset_profile=资产画像"},
              "incidentId":{"type":"integer","description":"timeline: 可选事件ID；省略时查看当前会话"},
              "sessionId":{"type":"string","description":"timeline: 可选会话ID（数字或 agent-数字）"},
              "assetId":{"type":"string","description":"asset_profile: 资产ID，如 ssh:web01 / app:order-service / cert:..."},
              "limit":{"type":"integer","description":"timeline 默认200上限500；asset_profile 默认10上限50"}
            },"required":["action"]}
            """,
            async (args, ct) =>
            {
                var action = (ArgumentReader.ReadString(args, "action") ?? string.Empty).Trim().ToLowerInvariant();
                object? result = action switch
                {
                    "timeline" => await timelineTool.InvokeAsync(new Dictionary<string, object?>
                    {
                        ["incidentId"] = ArgumentReader.ReadInt(args, "incidentId"),
                        ["sessionId"] = ArgumentReader.ReadString(args, "sessionId"),
                        ["limit"] = ArgumentReader.ReadInt(args, "limit")
                    }, ct),
                    "asset_profile" => await assetProfileTool.InvokeAsync(new Dictionary<string, object?>
                    {
                        ["assetId"] = ArgumentReader.ReadString(args, "assetId"),
                        ["limit"] = ArgumentReader.ReadInt(args, "limit")
                    }, ct),
                    _ => "错误: action 仅支持 timeline/asset_profile"
                };
                return result?.ToString() ?? string.Empty;
            });

        return new LocalAgentTool(function, readOnly: true);
    }
}
