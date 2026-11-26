using System.Text.Json;
using LitSSHmcp.McpServer.Services;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

/// <summary>tools/list schema 规范化：布尔 schema 必须改写为等价对象 schema。</summary>
public class McpSchemaNormalizerTests
{
    [Fact]
    public void Boolean_property_schemas_are_normalized_without_touching_boolean_keywords()
    {
        const string json = """
            {
              "type": "object",
              "properties": {
                "any": true,
                "never": false,
                "nested": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "data": true
                    }
                  }
                }
              },
              "uniqueItems": true,
              "additionalProperties": true
            }
            """;

        using var document = JsonDocument.Parse(json);
        var normalized = McpSchemaNormalizer.Normalize(document.RootElement);

        var any = normalized.GetProperty("properties").GetProperty("any");
        Assert.Equal(JsonValueKind.Object, any.ValueKind);
        Assert.Empty(any.EnumerateObject());

        var never = normalized.GetProperty("properties").GetProperty("never");
        Assert.Equal(JsonValueKind.Object, never.ValueKind);
        Assert.True(never.TryGetProperty("not", out _));

        var data = normalized.GetProperty("properties").GetProperty("nested")
            .GetProperty("items").GetProperty("properties").GetProperty("data");
        Assert.Equal(JsonValueKind.Object, data.ValueKind);

        // uniqueItems 是布尔数据关键字，不是 schema，必须保持不变。
        Assert.True(normalized.GetProperty("uniqueItems").GetBoolean());
        Assert.Equal(JsonValueKind.Object, normalized.GetProperty("additionalProperties").ValueKind);
    }
}
