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
    private const int DefaultMaxResultChars = 4000;

    public static IReadOnlyList<IAgentTool> Create(SpillStore store, int maxResultChars = DefaultMaxResultChars)
    {
        // 0 = 不做工具结果截断（沿用全局语义）；此时不主动限制 spill 读取总量。
        var budget = maxResultChars <= 0 ? int.MaxValue : Math.Max(500, maxResultChars);
        var function = new LocalFunction(
            "spill",
            "大工具结果落盘：action=list 列出句柄；action=read 分段读取；action=grep 按正则搜索（单行大 JSON 会返回匹配点前后上下文窗口）。handle 可传 spill://xxx 或文件名。",
            """{"type":"object","properties":{"action":{"type":"string","enum":["list","read","grep","json"],"description":"list=列出句柄, read=分段读取(默认), grep=正则搜索"},"handle":{"type":"string","description":"read/grep: 句柄(spill://xxx 或文件名)"},"offset":{"type":"integer","description":"read: 起始行(0 起), 默认 0"},"limit":{"type":"integer","description":"read: 读取行数, 默认 200, 上限 2000"},"pattern":{"type":"string","description":"grep: 正则表达式"},"path":{"type":"string","description":"json: path, e.g. data.sections.resource"},"max":{"type":"integer","description":"grep: 最多返回条数, 默认 100"}},"required":[]}""",
            args =>
            {
                var action = (ArgumentReader.ReadString(args, "action") ?? "read").Trim().ToLowerInvariant();
                return action switch
                {
                    "list" => store.List() is { Count: > 0 } items
                        ? string.Join('\n', items)
                        : "(无落盘结果)",
                    "read" => Read(store, ArgumentReader.ReadString(args, "handle"),
                        ArgumentReader.ReadInt(args, "offset"), ArgumentReader.ReadInt(args, "limit"), budget),
                    "grep" => Grep(store, ArgumentReader.ReadString(args, "handle"),
                        ArgumentReader.ReadString(args, "pattern"), ArgumentReader.ReadInt(args, "max"), budget),
                    "json" => ReadJson(store, ArgumentReader.ReadString(args, "handle"),
                        ArgumentReader.ReadString(args, "path"), ArgumentReader.ReadInt(args, "max"), budget),
                    _ => "错误: action 仅支持 list/read/grep"
                };
            });

        return new IAgentTool[] { new LocalAgentTool(function, readOnly: true) };
    }

    private static string Read(SpillStore store, string? handle, int? offset, int? limit, int maxResultChars)
    {
        var path = store.Resolve(handle);
        if (path is null || !File.Exists(path))
            return "(句柄无效或文件不存在)";

        var start = Math.Max(0, offset ?? 0);
        var count = Math.Clamp(limit ?? 200, 1, MaxReadLines);
        var bodyBudget = Math.Max(200, maxResultChars - 360);
        var sb = new StringBuilder();
        var total = 0;
        var lastEmitted = start - 1;
        var longLineTruncated = false;

        foreach (var raw in File.ReadLines(path))
        {
            if (total >= start && total < start + count)
            {
                var lineTruncated = raw.Length > MaxLineChars;
                var line = lineTruncated ? raw[..MaxLineChars] + "…" : raw;
                var formatted = $"{total}: {line}";
                if (sb.Length + formatted.Length + Environment.NewLine.Length > bodyBudget && lastEmitted >= start)
                {
                    // 已达字符预算：停止追加，但继续扫描以统计总行数（与旧实现一致，仅多一次遍历）。
                }
                else if (sb.Length + formatted.Length + Environment.NewLine.Length > bodyBudget)
                {
                    // 首行就超过预算：强制返回截断的一行，保证 offset 能继续前进。
                    var firstLineBudget = Math.Max(100, bodyBudget - 32);
                    sb.AppendLine($"{total}: {(line.Length <= firstLineBudget ? line : line[..firstLineBudget] + "…")}");
                    lastEmitted = total;
                    longLineTruncated = true;
                }
                else
                {
                    sb.AppendLine(formatted);
                    lastEmitted = total;
                    longLineTruncated |= lineTruncated;
                }
            }
            total++;
        }
        if (start >= total)
            return $"(偏移 {start} 超出范围, 共 {total} 行)";

        var end = lastEmitted;
        var next = end + 1;
        var more = next < total;
        var name = Path.GetFileName(path);
        var header = $"[{name} 共 {total} 行; 已返回 {start}..{end}";
        header += more ? $"; 下次 offset={next}" : "; 已到末尾";
        if (longLineTruncated)
            header += "; 存在超长行已截断, 建议 action=grep 精确定位";
        header += "]";
        return (header + Environment.NewLine + sb.ToString()).TrimEnd();
    }

    private static readonly System.Text.Json.JsonSerializerOptions PrettyJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private static string ReadJson(SpillStore store, string? handle, string? path, int? max, int maxResultChars)
    {
        var file = store.Resolve(handle);
        if (file is null || !File.Exists(file))
            return "(句柄无效或文件不存在)";

        System.Text.Json.Nodes.JsonNode? root;
        try
        {
            root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file));
        }
        catch (System.Text.Json.JsonException ex)
        {
            return "(不是可解析的 JSON) " + ex.Message;
        }
        if (root is null)
            return "(空 JSON)";

        System.Text.Json.Nodes.JsonNode? current = root;
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var raw in path.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var segment = raw.Trim();
                var bracket = segment.IndexOf('[');
                var key = bracket >= 0 ? segment[..bracket] : segment;
                if (key.Length > 0)
                {
                    current = current is System.Text.Json.Nodes.JsonObject obj && obj.TryGetPropertyValue(key, out var child)
                        ? child
                        : null;
                }
                if (bracket >= 0 && segment.EndsWith(']'))
                {
                    var indexText = segment[(bracket + 1)..^1];
                    if (!int.TryParse(indexText, out var index) ||
                        current is not System.Text.Json.Nodes.JsonArray array ||
                        index < 0 || index >= array.Count)
                        return $"(JSON 路径不存在: {path})";
                    current = array[index];
                }
                if (current is null)
                    return $"(JSON 路径不存在: {path})";
            }
        }

        var budget = Math.Clamp(max ?? maxResultChars, 100, Math.Max(100, maxResultChars));
        if (current is System.Text.Json.Nodes.JsonObject or System.Text.Json.Nodes.JsonArray)
        {
            var json = current.ToJsonString(PrettyJson);
            if (string.IsNullOrWhiteSpace(path) && current is System.Text.Json.Nodes.JsonObject rootObj)
                return SummarizeJsonObject(rootObj) + "\n[提示] 用 path 指定子节点，或 action=read 分段读取。";
            return json.Length <= budget
                ? json
                : json[..budget] + $"\n…[JSON 结果超过 {budget} 字符，已截断；请缩小 path 或改用 action=read 分段读取]";
        }

        return current.ToJsonString(PrettyJson);
    }

    private static string SummarizeJsonObject(System.Text.Json.Nodes.JsonObject obj)
    {
        var parts = new List<string>();
        foreach (var kv in obj)
        {
            var type = kv.Value switch
            {
                System.Text.Json.Nodes.JsonObject => "object",
                System.Text.Json.Nodes.JsonArray array => $"array[{array.Count}]",
                null => "null",
                _ => "value"
            };
            parts.Add($"{kv.Key}={type}");
        }
        return "根节点键: " + string.Join("; ", parts);
    }

    private static string Grep(SpillStore store, string? handle, string? pattern, int? max, int maxResultChars)
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
        var bodyBudget = Math.Max(200, maxResultChars - 240);
        var sb = new StringBuilder();
        var hits = 0;
        var budgetHit = false;
        var index = 0;
        foreach (var raw in File.ReadLines(path))
        {
            if (regex.IsMatch(raw))
            {
                if (raw.Length > MaxLineChars)
                {
                    // 单行大 JSON：不能整行返回；按匹配位置返回上下文窗口。
                    foreach (Match match in regex.Matches(raw))
                    {
                        var start = Math.Max(0, match.Index - 200);
                        var length = Math.Min(raw.Length - start, 600);
                        var window = raw.Substring(start, length);
                        var entry = $"{index}@{match.Index}: ...{window}...";
                        if (sb.Length + entry.Length + Environment.NewLine.Length > bodyBudget)
                        {
                            budgetHit = true;
                            break;
                        }
                        sb.AppendLine(entry);
                        if (++hits >= take)
                            break;
                    }
                }
                else
                {
                    var entry = $"{index}: {raw}";
                    if (sb.Length + entry.Length + Environment.NewLine.Length > bodyBudget)
                    {
                        budgetHit = true;
                        break;
                    }
                    sb.AppendLine(entry);
                    hits++;
                }
            }
            index++;
            if (hits >= take || budgetHit)
                break;
        }
        if (hits == 0)
            return budgetHit ? "(结果超出字符预算; 请细化 pattern 或降低 max)" : "(无匹配)";

        var footer = budgetHit
            ? $"\n…[已达字符预算 {maxResultChars}; 可能还有更多匹配, 请细化 pattern 或降低 max]"
            : string.Empty;
        return sb.ToString().TrimEnd() + footer;
    }
}
