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
                        ArgumentReader.ReadInt(args, "offset"), ArgumentReader.ReadInt(args, "limit")),
                    "grep" => Grep(store, ArgumentReader.ReadString(args, "handle"),
                        ArgumentReader.ReadString(args, "pattern"), ArgumentReader.ReadInt(args, "max")),
                    "json" => ReadJson(store, ArgumentReader.ReadString(args, "handle"),
                        ArgumentReader.ReadString(args, "path"), ArgumentReader.ReadInt(args, "max")),
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

    private static readonly System.Text.Json.JsonSerializerOptions PrettyJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private static string ReadJson(SpillStore store, string? handle, string? path, int? max)
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

        var budget = Math.Clamp(max ?? 8000, 500, 50000);
        if (current is System.Text.Json.Nodes.JsonObject or System.Text.Json.Nodes.JsonArray)
        {
            var json = current.ToJsonString(PrettyJson);
            if (string.IsNullOrWhiteSpace(path) && current is System.Text.Json.Nodes.JsonObject rootObj)
                return SummarizeJsonObject(rootObj) + "\n[提示] 用 path 指定子节点，或 action=read 分段读取。";
            return json.Length <= budget
                ? json
                : json[..budget] + $"\n…[JSON 结果超过 {budget} 字符，已截断；缩小 path 或提高 max]";
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
                if (raw.Length > MaxLineChars)
                {
                    // 单行大 JSON：不能整行返回；按匹配位置返回上下文窗口。
                    foreach (Match match in regex.Matches(raw))
                    {
                        var start = Math.Max(0, match.Index - 200);
                        var length = Math.Min(raw.Length - start, 600);
                        var window = raw.Substring(start, length);
                        sb.AppendLine($"{index}@{match.Index}: ...{window}...");
                        if (++hits >= take)
                            break;
                    }
                }
                else
                {
                    sb.AppendLine($"{index}: {raw}");
                    hits++;
                }
            }
            index++;
            if (hits >= take)
                break;
        }
        return hits == 0 ? "(无匹配)" : sb.ToString().TrimEnd();
    }
}
