using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 提供给模型的**核心记忆工具**（本地、非 MCP）：结构化运维经验的写入/更新/查询/删除。
/// 仅在配置 agent.memory.enabled=true 时由 <see cref="AgentRuntime"/> 注册；
/// 更新语义（同 key 覆盖+频次+1）与向量召回均在 <see cref="AgentMemoryService"/> 内完成。
/// </summary>
public static class MemoryTools
{
    public static IReadOnlyList<IAgentTool> Create(AgentMemoryService memory)
    {
        var write = new LocalFunction(
            "ops_memory_write",
            "只写入/修正/删除可跨事件复用的运维经验(任务复盘/脚本/资产经验): action=save 新增/合并; action=update 修正或标记 superseded; action=delete 删除; action=drafts 查看由历史事件自动蒸馏出的待确认经验草稿; action=confirm_draft 确认草稿(可传 key 改成语义化键)并写入核心经验; action=reject_draft 丢弃草稿。排查前建议先 action=drafts 看看有没有可直接确认的经验。单次诊断/变更记录用 ops_diagnosis_write，不要重复写 incident。内容含密码/密钥/令牌会被拒绝，过短或高度相似时自动合并; Web 普通成员写私有记忆, 管理员写团队记忆",
            """{"type":"object","properties":{"action":{"type":"string","enum":["save","update","delete","drafts","confirm_draft","reject_draft"],"description":"save=新增/合并, update=修正字段或标记过时, delete=删除, drafts=列出待确认经验草稿, confirm_draft=确认草稿写入核心经验, reject_draft=丢弃草稿"},"kind":{"type":"string","enum":["task","script","asset"],"description":"类别: task=可复用排查步骤(通用), script=脚本工具, asset=单资产事实/经验"},"key":{"type":"string","description":"语义唯一键；资产专属经验用 `<现象>:<资产ID>`（资产ID从资产清单原样复制）, 通用步骤不要带资产ID；confirm_draft 时可传新 key 覆盖草稿默认键"},"content":{"type":"string","description":"save: 经验内容(现象→根因→验证成功的解决路径); update: 新内容(省略则不变); delete/drafts/confirm_draft/reject_draft: 忽略"},"assetIds":{"type":"string","description":"关联资产ID, 逗号分隔; 必须从系统提示的资产清单原样复制(ssh:/ds:/app:...), 不要写 web01 这类简称或主机名"},"tags":{"type":"string","description":"标签, 逗号分隔"},"status":{"type":"string","enum":["active","superseded"],"description":"update: active=有效, superseded=已过时(召回不再出现)"},"draftId":{"type":"integer","description":"confirm_draft/reject_draft: action=drafts 返回的草稿编号 #N"},"reason":{"type":"string","description":"reject_draft: 丢弃原因(可选)"}},"required":["action"]}""",
            async (args, ct) =>
            {
                var action = (ArgumentReader.ReadString(args, "action") ?? "save").Trim().ToLowerInvariant();
                var kind = ArgumentReader.ReadString(args, "kind") ?? string.Empty;
                var key = ArgumentReader.ReadString(args, "key") ?? string.Empty;
                var draftId = ArgumentReader.ReadInt(args, "draftId") ?? 0;
                return action switch
                {
                    "save" => await memory.SaveAsync(kind, key,
                        ArgumentReader.ReadString(args, "content") ?? string.Empty,
                        ArgumentReader.ReadString(args, "assetIds"),
                        ArgumentReader.ReadString(args, "tags"), ct),
                    "update" => await memory.UpdateAsync(kind, key,
                        ArgumentReader.ReadString(args, "content"),
                        ArgumentReader.ReadString(args, "assetIds"),
                        ArgumentReader.ReadString(args, "tags"),
                        ArgumentReader.ReadString(args, "status"), ct),
                    "delete" => await memory.DeleteAsync(kind, key, ct),
                    "drafts" => await ListDraftsAsync(memory, ct),
                    "confirm_draft" => await ConfirmDraftAsync(memory, draftId, key, ct),
                    "reject_draft" => await RejectDraftAsync(memory, draftId,
                        ArgumentReader.ReadString(args, "reason"), ct),
                    _ => "错误: action 仅支持 save/update/delete/drafts/confirm_draft/reject_draft"
                };
            });

        var list = new LocalFunction(
            "ops_memory_list",
            "列出核心记忆(任务复盘/脚本/资产经验): 可按 kind 或资产ID 过滤; 开始排查前先查同类经验",
            """{"type":"object","properties":{"kind":{"type":"string","enum":["task","script","asset"],"description":"只看该类别(省略=全部)"},"assetId":{"type":"string","description":"只看涉及该资产的记忆(从资产清单原样复制的资产ID)"},"limit":{"type":"integer","description":"最多返回条数, 默认50, 上限200"}},"required":[]}""",
            async (args, ct) =>
            {
                var limit = ArgumentReader.ReadInt(args, "limit") ?? 50;
                return await memory.ListAsync(
                    ArgumentReader.ReadString(args, "kind"),
                    ArgumentReader.ReadString(args, "assetId"),
                    limit,
                    ct);
            });

        return new IAgentTool[]
        {
            new LocalAgentTool(list, readOnly: true),
            new LocalAgentTool(write),
        };
    }

    private static async Task<string> ListDraftsAsync(AgentMemoryService memory, CancellationToken ct)
    {
        var drafts = await memory.Store.ListDraftsAsync(20, ct);
        if (drafts.Count == 0)
            return "(暂无待确认经验草稿)";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"待确认经验草稿 {drafts.Count} 条（由历史事件自动蒸馏；确认用 action=confirm_draft 并传 draftId）：");
        foreach (var d in drafts)
        {
            var content = d.Content.Length <= 160 ? d.Content : d.Content[..160] + "…";
            sb.AppendLine($"- #{d.Id} [{d.Kind}:{d.Key}] 置信度={d.Confidence:0.##} 资产={d.AssetIds} | {content}");
        }
        return sb.ToString().Trim();
    }

    private static async Task<string> ConfirmDraftAsync(AgentMemoryService memory, int draftId,
        string? overrideKey, CancellationToken ct)
    {
        if (draftId <= 0)
            return "错误: confirm_draft 需要正整数 draftId（先用 action=drafts 查看）";
        var drafts = await memory.Store.ListDraftsAsync(100, ct);
        var draft = drafts.FirstOrDefault(d => d.Id == draftId);
        if (draft is null)
            return $"错误: 未找到待确认草稿 #{draftId}";
        var key = string.IsNullOrWhiteSpace(overrideKey) ? draft.Key : overrideKey.Trim();
        // 走 SaveAsync 复用全部质量门：敏感词拒绝、过短拒绝、近重复合并、资产冲突保护。
        var result = await memory.SaveAsync(draft.Kind, key, draft.Content,
            string.IsNullOrWhiteSpace(draft.AssetIds) ? null : draft.AssetIds, "auto-distilled", ct);
        if (result.StartsWith("错误", StringComparison.Ordinal))
            return result;
        await memory.Store.SetDraftStatusAsync(draft.Id, "applied", $"ops_memory_save:{key}", ct);
        return $"已确认草稿 #{draft.Id} → {result}（key={key}）";
    }

    private static async Task<string> RejectDraftAsync(AgentMemoryService memory, int draftId,
        string? reason, CancellationToken ct)
    {
        if (draftId <= 0)
            return "错误: reject_draft 需要正整数 draftId";
        var ok = await memory.Store.SetDraftStatusAsync(draftId, "rejected", reason ?? string.Empty, ct);
        return ok ? $"已丢弃草稿 #{draftId}" : $"错误: 未找到待确认草稿 #{draftId}";
    }
}
