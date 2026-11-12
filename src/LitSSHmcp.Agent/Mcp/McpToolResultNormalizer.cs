using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace LitSSHmcp.Agent;

/// <summary>
/// 把 MCP 的 <see cref="CallToolResult"/> 归一化成一份模型上下文文本：
/// 优先 structuredContent，缺失时回退 content 文本，避免 content + structuredContent 两份同构内容重复占用 token。
/// </summary>
internal static class McpToolResultNormalizer
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public sealed record Normalized(string Text, bool IsError);

    public static Normalized Normalize(object? result)
    {
        if (result is not CallToolResult call)
            return new Normalized(result?.ToString() ?? string.Empty, false);

        var structured = ToText(call.StructuredContent);
        var content = ExtractContent(call.Content);
        var isError = call.IsError == true;

        string text;
        if (isError)
        {
            // 错误时 content 通常带可操作的错误细节；结构化结果只作为补充。
            text = !string.IsNullOrWhiteSpace(content) ? content : structured;
            if (string.IsNullOrWhiteSpace(text))
                text = "工具返回错误（无详细信息）";
        }
        else
        {
            text = !string.IsNullOrWhiteSpace(structured) ? structured : content;
        }

        return new Normalized(text, isError);
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
