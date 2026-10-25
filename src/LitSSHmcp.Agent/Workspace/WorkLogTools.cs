using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 提供给模型的桌面端工作日志工具（本地、非 MCP）：仅在启用工作日志且允许模型复盘时注册。
/// 路径由宿主固定在当天日志文件，模型只能追加文字，不能选择/改写路径。
/// </summary>
public static class WorkLogTools
{
    public const string WriteToolName = "ops_worklog_write";

    public static IReadOnlyList<IAgentTool> Create(WorkLogStore store)
    {
        var write = new LocalFunction(
            WriteToolName,
            "追加一条今日工作日志复盘（桌面端）。仅在完成含变更操作的非平凡任务后调用；用 3-5 行写清 目标 → 关键操作 → 结果/影响 → 后续，不要粘贴原始输出或重复操作流水。",
            """{"type":"object","properties":{"content":{"type":"string","description":"复盘正文(3-5行, 简体中文)"}},"required":["content"]}""",
            async (args, ct) => await store.AppendNoteAsync(
                ArgumentReader.ReadString(args, "content"), ct));

        return new IAgentTool[] { new LocalAgentTool(write) };
    }
}
