using System.Text.Json;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Agent;

/// <summary>
/// MCP 开关两级模型（评估报告 D8 / §6）的**启用层**策略：
/// 配置层（全局 <c>agent.mcpServers[].Enabled</c>）决定注册表与默认集合；启用层（会话级子集）
/// 只在全局启用集合内**收窄**，全局停用恒出局（配置层优先，会话不能反向开启）。
/// 子集持久化为会话上的 JSON id 数组；空/缺失 = 继承全局默认集合；首条消息后锁定，运行中不可改。
/// </summary>
public static class McpSubsetPolicy
{
    /// <summary>解析会话子集 JSON。空串 / null / 非法 JSON 一律回退 null（=继承全局启用集合，fail-safe 只会多给不会少给）。</summary>
    public static IReadOnlyCollection<string>? ParseSubset(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(json);
            if (ids is null || ids.Count == 0)
                return null;
            var set = new HashSet<string>(ids.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
            return set.Count == 0 ? null : set;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>序列化会话子集（null/空集合 = 继承态，存空串）。去重并按序排序，保证存储形态稳定。</summary>
    public static string SerializeSubset(IEnumerable<string>? ids)
    {
        if (ids is null)
            return string.Empty;
        var sorted = ids.Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        return sorted.Count == 0 ? string.Empty : JsonSerializer.Serialize(sorted);
    }

    /// <summary>
    /// 会话**生效**服务器列表：基线 = 全局 Enabled；子集非 null 时再与子集取交。
    /// 保持配置顺序；子集里的未知 id（配置已删除）自然出局。
    /// </summary>
    public static List<AgentMcpServerConfig> EffectiveServers(
        IReadOnlyList<AgentMcpServerConfig> servers, IReadOnlyCollection<string>? subset)
    {
        var globallyOn = servers.Where(s => s.Enabled).ToList();
        if (subset is null)
            return globallyOn;
        return globallyOn.Where(s => subset.Contains(s.Id, StringComparer.Ordinal)).ToList();
    }

    /// <summary>
    /// 启用层是否可编辑（与工作目录同一锁定原则）：会话已启动（已落库 / 工作目录已分配 / 已有轮次）
    /// 或任务运行中 → 锁定，改动下个会话生效。落库即视为"首条消息已发生"，清屏/清空消息不解锁。
    /// </summary>
    public static bool EnableLayerEditable(long sessionId, string? workspaceDir, int turnCount, bool running)
        => sessionId <= 0 && string.IsNullOrWhiteSpace(workspaceDir) && turnCount <= 0 && !running;
}
