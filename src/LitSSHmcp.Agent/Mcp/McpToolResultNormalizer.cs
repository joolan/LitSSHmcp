using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace LitSSHmcp.Agent;

/// <summary>
/// 把 MCP 的 <see cref="CallToolResult"/> 统一成一层模型可读文本。
/// 优先 structuredContent；缺失时取 content 文本块，避免 content + structuredContent 两份同样内容重复占用 token。
/// </summary>
public static class McpToolResultNormalizer
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public sealed record Normalized(string Text, bool IsError);

    public static Normalized Normalize(object? result)
    {
        if (result is CallToolResult call)
        {
            return FromEnvelope(ToText(call.StructuredContent), ExtractContent(call.Content),
                call.IsError == true);
        }

        // MCP 客户端/宿主侧调用有时拿到的是 JSON 文本或 JsonElement，而不是 CallToolResult。
        // 这里也按 CallToolResult 信封解包，否则模型会看到额外一层 content/JSON 转义。
        if (result is JsonElement element)
            return NormalizeJson(element) ?? new Normalized(ToText(element), false);

        if (result is JsonNode node)
        {
            var nodeElement = JsonSerializer.SerializeToElement(node, CompactJson);
            return NormalizeJson(nodeElement) ?? new Normalized(ToText(node), false);
        }

        if (result is string text)
            return NormalizeText(text);

        var raw = result?.ToString() ?? string.Empty;
        return NormalizeText(raw);
    }

    private static Normalized FromEnvelope(string structured, string content, bool isError)
    {
        string text;
        if (isError)
        {
            // 出错时 content 通常是可操作细节，结构化内容只作为兜底。
            text = !string.IsNullOrWhiteSpace(content) ? content : structured;
            if (string.IsNullOrWhiteSpace(text))
                text = "工具返回错误，但无详细信息。";
        }
        else
        {
            text = !string.IsNullOrWhiteSpace(structured) ? structured : content;
        }

        return new Normalized(text, isError);
    }

    private static Normalized? NormalizeJson(JsonElement root, int depth = 0)
    {
        if (root.ValueKind == JsonValueKind.String)
            return NormalizeText(root.GetString() ?? string.Empty, depth + 1);

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var isError = TryGetProperty(root, "isError", out var errorNode) &&
                      errorNode.ValueKind is JsonValueKind.True;

        var hasContent = TryGetProperty(root, "content", out var contentNode) &&
                         contentNode.ValueKind == JsonValueKind.Array;
        var hasStructured = TryGetProperty(root, "structuredContent", out var structuredNode) &&
                            structuredNode.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

        // 只有像 CallToolResult 的对象才解包；普通业务 JSON 的 content 字段不能误伤。
        if (!hasContent && !hasStructured)
            return null;

        var structured = hasStructured ? ToText(structuredNode) : string.Empty;
        var content = hasContent ? ExtractContent(contentNode, depth) : string.Empty;
        if (string.IsNullOrWhiteSpace(structured) && string.IsNullOrWhiteSpace(content))
            return null;

        return FromEnvelope(structured, content, isError);
    }

    private static Normalized NormalizeText(string text, int depth = 0)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new Normalized(string.Empty, false);

        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[') || depth > 3)
            return new Normalized(text, false);

        try
        {
            using var doc = JsonDocument.Parse(text);
            return NormalizeJson(doc.RootElement, depth) ?? new Normalized(text, false);
        }
        catch (JsonException)
        {
            return new Normalized(text, false);
        }
    }

    private static string ExtractContent(IEnumerable<ContentBlock>? blocks)
    {
        if (blocks is null)
            return string.Empty;

        var parts = new List<string>();
        foreach (var block in blocks)
        {
            var text = block switch
            {
                TextContentBlock textBlock => textBlock.Text,
                null => null,
                _ => block.ToString()
            };
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(text);
        }
        return string.Join('\n', parts);
    }

    private static string ExtractContent(JsonElement content, int depth)
    {
        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
                continue;
            if (!TryGetProperty(block, "text", out var textNode) ||
                textNode.ValueKind != JsonValueKind.String)
                continue;

            var text = textNode.GetString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(text);
        }

        var joined = string.Join('\n', parts);
        return depth < 3 ? NormalizeText(joined, depth + 1).Text : joined;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
            return true;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string ToText(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        JsonNode node => node.ToJsonString(CompactJson),
        JsonElement element => element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.GetRawText(),
        _ => value.ToString() ?? string.Empty
    };
}
