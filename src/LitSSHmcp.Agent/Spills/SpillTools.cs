using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 大结果落盘句柄的读取工具（本地、非 MCP）：<c>spill(action=list|read|grep)</c>。
/// 工具结果过大时上下文里只放预览 + <c>spill://&lt;handle&gt;</c>，模型用这些工具按需分段读取。
/// </summary>
public static class SpillTools
{
    private const int MaxReadLines = 2000;
    private const int MaxLineChars = 2000;

    public static IReadOnlyList<IAgentTool> Create(SpillStore store)
    {
        var function = new LocalFunction(
            "spill",
            "大工具结果落盘：action=list 列出句柄；action=read 分段读取；action=grep 按正则搜索。handle 可传 spill://xxx 或文件名。",
            """{"type":"object","properties":{"action":{"type":"string","enum":["list","read","grep"],"description":"list=列出句柄, read=分段读取(默认), grep=正则搜索"},"handle":{"type":"string","description":"read/grep: 句柄(spill://xxx 或文件名)"},"offset":{"type":"integer","description":"read: 起始行(0 起), 默认 0"},"limit":{"type":"integer","description":"read: 读取行数, 默认 200, 上限 2000"},"pattern":{"type":"string","description":"grep: 正则表达式"},"max":{"type":"integer","description":"grep: 最多返回条数, 默认 100"}},"required":[]}""",
            args =>
            {
                var action = (ArgumentReader.ReadString(args, "action") ?? "read").Trim().ToLowerInvariant();
                return action switch
                {
                    "list" => store.List() is { Count: > 0 } items
                        ? string.Join('\n', items)
                        : "(无落盘结果)",
                    "read" => Read(store, ArgumentReader.ReadString(args, "handle"),
                        ArgumentReader.ReadInt(args, "offset"), ArgumentReader.ReadInt(args, "limit")),
                    "grep" => Grep(store, ArgumentReader.ReadString(args, "handle"),
                        ArgumentReader.ReadString(args, "pattern"), ArgumentReader.ReadInt(args, "max")),
                    _ => "错误: action 仅支持 list/read/grep"
                };
            });

        return new IAgentTool[] { new LocalAgentTool(function, readOnly: true) };
    }

    private static string Read(SpillStore store, string? handle, int? offset, int? limit)
    {
        var path = store.Resolve(handle);
        if (path is null || !File.Exists(path))
            return "(句柄无效或文件不存在)";

        var start = Math.Max(0, offset ?? 0);
        var count = Math.Clamp(limit ?? 200, 1, MaxReadLines);
        var sb = new StringBuilder();
        var total = 0;
        foreach (var raw in File.ReadLines(path))
        {
            if (total >= start && total < start + count)
            {
                var line = raw.Length > MaxLineChars ? raw[..MaxLineChars] + "…" : raw;
                sb.AppendLine($"{total}: {line}");
            }
            total++;
        }
        if (start >= total)
            return $"(偏移 {start} 超出范围, 共 {total} 行)";

        var header = $"[{path.Split(Path.DirectorySeparatorChar)[^1]} 共 {total} 行, 显示 {start}..{Math.Min(total, start + count) - 1}]";
        return (header + Environment.NewLine + sb.ToString()).TrimEnd();
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
        var sb = new StringBuilder();
        var hits = 0;
        var index = 0;
        foreach (var raw in File.ReadLines(path))
        {
            if (regex.IsMatch(raw))
            {
                var line = raw.Length > MaxLineChars ? raw[..MaxLineChars] + "…" : raw;
                sb.AppendLine($"{index}: {line}");
                if (++hits >= take)
                    break;
            }
            index++;
        }
        return hits == 0 ? "(无匹配)" : sb.ToString().TrimEnd();
    }
}
