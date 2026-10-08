using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 大结果落盘句柄的读取工具（本地、非 MCP）：<c>spill_list</c> / <c>spill_read</c> / <c>spill_grep</c>。
/// 工具结果过大时上下文里只放预览 + <c>spill://&lt;handle&gt;</c>，模型用这些工具按需分段读取。
/// </summary>
public static class SpillTools
{
    private const int MaxReadLines = 2000;
    private const int MaxLineChars = 2000;

    public static IReadOnlyList<IAgentTool> Create(SpillStore store)
    {
        var list = new LocalFunction(
            "spill_list",
            "列出已落盘的大工具结果句柄(按时间倒序)",
            """{"type":"object","properties":{}}""",
            _ => store.List() is { Count: > 0 } items ? string.Join('\n', items) : "(无落盘结果)");

        var read = new LocalFunction(
            "spill_read",
            "分段读取此前落盘的大工具结果; 传 handle(如 spill://xxx 或文件名), 可指定行偏移与条数",
            """{"type":"object","properties":{"handle":{"type":"string","description":"句柄(spill://xxx 或文件名)"},"offset":{"type":"integer","description":"起始行(0 起), 默认 0"},"limit":{"type":"integer","description":"读取行数, 默认 200, 上限 2000"}},"required":["handle"]}""",
            args => Read(store, ArgumentReader.ReadString(args, "handle"), ArgumentReader.ReadInt(args, "offset"), ArgumentReader.ReadInt(args, "limit")));

        var grep = new LocalFunction(
            "spill_grep",
            "在落盘结果中按正则搜索匹配行(返回行号+内容, 最多 max 条)",
            """{"type":"object","properties":{"handle":{"type":"string","description":"句柄(spill://xxx 或文件名)"},"pattern":{"type":"string","description":"正则表达式"},"max":{"type":"integer","description":"最多返回条数, 默认 100"}},"required":["handle","pattern"]}""",
            args => Grep(store, ArgumentReader.ReadString(args, "handle"), ArgumentReader.ReadString(args, "pattern"), ArgumentReader.ReadInt(args, "max")));

        return new IAgentTool[]
        {
            new LocalAgentTool(list, readOnly: true),
            new LocalAgentTool(read, readOnly: true),
            new LocalAgentTool(grep, readOnly: true)
        };
    }

    private static string Read(SpillStore store, string? handle, int? offset, int? limit)
    {
        var path = store.Resolve(handle);
        if (path is null || !File.Exists(path))
            return "(句柄无效或文件不存在)";

        var lines = File.ReadAllLines(path);
        var start = Math.Max(0, offset ?? 0);
        var count = Math.Clamp(limit ?? 200, 1, MaxReadLines);
        if (start >= lines.Length)
            return $"(偏移 {start} 超出范围, 共 {lines.Length} 行)";

        var sb = new StringBuilder();
        sb.AppendLine($"[{path.Split(Path.DirectorySeparatorChar)[^1]} 共 {lines.Length} 行, 显示 {start}..{Math.Min(lines.Length, start + count) - 1}]");
        for (var i = start; i < Math.Min(lines.Length, start + count); i++)
        {
            var line = lines[i];
            if (line.Length > MaxLineChars)
                line = line[..MaxLineChars] + "…";
            sb.AppendLine($"{i}: {line}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Grep(SpillStore store, string? handle, string? pattern, int? max)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return "(pattern 不能为空)";
        var path = store.Resolve(handle);
        if (path is null || !File.Exists(path))
            return "(句柄无效或文件不存在)";

        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.IgnoreCase); }
        catch (Exception ex) { return $"(正则无效) {ex.Message}"; }

        var take = Math.Clamp(max ?? 100, 1, 1000);
        var lines = File.ReadAllLines(path);
        var sb = new StringBuilder();
        var hits = 0;
        for (var i = 0; i < lines.Length && hits < take; i++)
        {
            if (!regex.IsMatch(lines[i]))
                continue;
            var line = lines[i].Length > MaxLineChars ? lines[i][..MaxLineChars] + "…" : lines[i];
            sb.AppendLine($"{i}: {line}");
            hits++;
        }
        return hits == 0 ? "(无匹配)" : sb.ToString().TrimEnd();
    }
}
