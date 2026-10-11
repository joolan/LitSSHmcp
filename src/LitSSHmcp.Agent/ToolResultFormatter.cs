using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LitSSHmcp.Agent;

/// <summary>
/// 工具结果/入参的 JSON 规范化。MCP C# SDK 默认使用“HTML 安全”编码器序列化 JSON，
/// 中文会写成 <c>\uXXXX</c>、引号写成 <c>\u0022</c>（数据本身正确，但费 token 且界面难读）。
/// 这里把 JSON 解析后重新序列化：非 ASCII 不转义，并把“字符串里再嵌一层 JSON”的内容展开。
/// <list type="bullet">
///   <item><b>喂给模型</b>用紧凑模式 <see cref="Normalize"/>（最少 token）；</item>
///   <item><b>界面展示</b>用 <see cref="Format"/>（缩进，易读）。</item>
/// </list>
/// </summary>
public static class ToolResultFormatter
{
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    /// <summary>规范化供模型使用的工具结果：去掉 <c>\uXXXX</c> 转义、展开内嵌 JSON、保持紧凑（省 token）。</summary>
    public static string Normalize(string? raw) => Format(raw, pretty: false);

    /// <summary>把 JSON 排版为可读文本；非 JSON 或解析失败时原样返回。</summary>
    public static string Format(string? raw, bool pretty)
    {
        if (string.IsNullOrEmpty(raw))
            return raw ?? string.Empty;

        var first = raw.TrimStart();
        if (first.Length == 0 || (first[0] != '{' && first[0] != '['))
            return raw;

        try
        {
            var node = JsonNode.Parse(raw);
            return node is null ? raw : Unwrap(node)!.ToJsonString(pretty ? PrettyOptions : CompactOptions);
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    /// <summary>递归展开“值本身又是 JSON 文本”的字符串（如 MCP content[].text），便于阅读并省去转义。</summary>
    private static JsonNode? Unwrap(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var current = obj[key];
                    var unwrapped = Unwrap(current);
                    if (!ReferenceEquals(current, unwrapped))
                        obj[key] = unwrapped;   // 仅在被替换时重新挂载，避免“节点已有父级”异常
                }
                return obj;

            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var current = arr[i];
                    var unwrapped = Unwrap(current);
                    if (!ReferenceEquals(current, unwrapped))
                        arr[i] = unwrapped;
                }
                return arr;

            case JsonValue value when value.TryGetValue(out string? text) && text is not null:
                var trimmed = text.TrimStart();
                if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[') && TryParse(text, out var inner))
                    return Unwrap(inner);
                return value;

            default:
                return node;
        }
    }

    private static bool TryParse(string text, out JsonNode? node)
    {
        node = null;
        try { node = JsonNode.Parse(text); return node is not null; }
        catch { return false; }
    }
}
