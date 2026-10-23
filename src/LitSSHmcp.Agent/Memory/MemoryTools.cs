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
            "写入或修正核心运维经验记忆(任务复盘/脚本/资产经验): mode=save 新增/合并(同 kind+key 覆盖最新有效路径并频次+1); mode=update 修正字段或把过时经验标记 superseded(不再召回); 内容含密码/密钥/令牌会被拒绝, 过短或与既有条目高度相似时自动合并; Web 普通成员写入私有记忆, 管理员写入团队记忆",
            """{"type":"object","properties":{"mode":{"type":"string","enum":["save","update"],"description":"save=新增/合并(默认), update=修正字段或标记过时"},"kind":{"type":"string","enum":["task","script","asset"],"description":"类别: task=任务复盘, script=脚本工具, asset=资产经验"},"key":{"type":"string","description":"语义唯一键, 如 磁盘满:ssh:web01"},"content":{"type":"string","description":"save: 经验内容(现象→根因→验证成功的解决路径); update: 新内容(省略则不变)"},"assetIds":{"type":"string","description":"关联资产ID, 逗号分隔, 如 ssh:web01,ds:mysql01"},"tags":{"type":"string","description":"标签, 逗号分隔"},"status":{"type":"string","enum":["active","superseded"],"description":"update: active=有效, superseded=已过时(召回不再出现)"}},"required":["mode","kind","key"]}""",
            async (args, ct) =>
            {
                var mode = (ArgumentReader.ReadString(args, "mode") ?? "save").Trim().ToLowerInvariant();
                var kind = ArgumentReader.ReadString(args, "kind") ?? string.Empty;
                var key = ArgumentReader.ReadString(args, "key") ?? string.Empty;
                return mode switch
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
                    _ => "错误: mode 仅支持 save/update"
                };
            });

        var list = new LocalFunction(
            "ops_memory_list",
            "列出核心记忆(任务复盘/脚本/资产经验): 可按 kind 或资产ID 过滤; 开始排查前先查同类经验",
            """{"type":"object","properties":{"kind":{"type":"string","enum":["task","script","asset"],"description":"只看该类别(省略=全部)"},"assetId":{"type":"string","description":"只看涉及该资产的记忆, 如 ssh:web01"},"limit":{"type":"integer","description":"最多返回条数, 默认50, 上限200"}},"required":[]}""",
            async (args, ct) =>
            {
                var limit = ArgumentReader.ReadInt(args, "limit") ?? 50;
                return await memory.ListAsync(
                    ArgumentReader.ReadString(args, "kind"),
                    ArgumentReader.ReadString(args, "assetId"),
                    limit,
                    ct);
            });

        var delete = new LocalFunction(
            "ops_memory_delete",
            "删除一条核心记忆(按 kind+key); 仅在用户要求清理或条目明显错误时使用; 团队记忆仅管理员可删",
            """{"type":"object","properties":{"kind":{"type":"string","enum":["task","script","asset"],"description":"类别"},"key":{"type":"string","description":"语义唯一键"}},"required":["kind","key"]}""",
            async (args, ct) => await memory.DeleteAsync(
                ArgumentReader.ReadString(args, "kind") ?? string.Empty,
                ArgumentReader.ReadString(args, "key") ?? string.Empty,
                ct));

        return new IAgentTool[]
        {
            new LocalAgentTool(list, readOnly: true),
            new LocalAgentTool(write),
            new LocalAgentTool(delete),
        };
    }
}
