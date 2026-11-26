using System.Text.Json;
using System.Text.Json.Nodes;

namespace LitSSHmcp.McpServer.Services;

/// <summary>
/// tools/list 的 JSON Schema 规范化：
/// JSON Schema 允许布尔 schema（true/false），但部分严格的 MCP 客户端要求
/// properties.* 必须是对象 schema，遇到 <c>"result": true</c> 会把整批工具判为非法。
/// 这里把所有 schema 位置上的布尔值改写为等价对象 schema（true→{}，false→{"not":{}}），
/// 只处理 schema 关键字，避免误改 <c>uniqueItems</c> 等布尔数据字段。
/// </summary>
internal static class McpSchemaNormalizer
{
    private static readonly JsonNode FalseSchema = JsonNode.Parse("""{"not":{}}""")!;

    /// <summary>把 outputSchema/inputSchema 中的布尔 schema 规范化为对象 schema。</summary>
    public static JsonElement Normalize(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return schema;

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(schema.GetRawText()) as JsonObject;
        }
        catch (JsonException)
        {
            return schema;
        }

        if (root is null)
            return schema;

        NormalizeSchemaObject(root);
        return JsonSerializer.SerializeToElement(root);
    }

    private static void NormalizeSchemaObject(JsonObject schema)
    {
        NormalizeSchemaMap(schema, "properties");
        NormalizeSchemaMap(schema, "patternProperties");
        NormalizeSchemaMap(schema, "dependentSchemas");
        NormalizeSchemaMap(schema, "$defs");
        NormalizeSchemaMap(schema, "definitions");

        foreach (var key in SingleSchemaKeywords)
        {
            if (schema.TryGetPropertyValue(key, out var child))
                schema[key] = NormalizeSchemaNode(child);
        }

        foreach (var key in SchemaArrayKeywords)
        {
            if (schema[key] is JsonArray array)
            {
                for (var i = 0; i < array.Count; i++)
                    array[i] = NormalizeSchemaNode(array[i]);
            }
        }
    }

    private static void NormalizeSchemaMap(JsonObject schema, string key)
    {
        if (schema[key] is not JsonObject map)
            return;

        foreach (var name in map.Select(pair => pair.Key).ToList())
            map[name] = NormalizeSchemaNode(map[name]);
    }

    private static JsonNode? NormalizeSchemaNode(JsonNode? node)
    {
        if (node is null)
            return null;

        if (node is JsonValue value && value.TryGetValue<bool>(out var boolean))
            return boolean ? new JsonObject() : FalseSchema.DeepClone();

        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
                array[i] = NormalizeSchemaNode(array[i]);
            return array;
        }

        if (node is JsonObject obj)
            NormalizeSchemaObject(obj);

        return node;
    }

    private static readonly string[] SingleSchemaKeywords =
    {
        "items", "contains", "additionalProperties", "unevaluatedProperties", "propertyNames",
        "not", "if", "then", "else", "contentSchema", "additionalItems", "unevaluatedItems"
    };

    private static readonly string[] SchemaArrayKeywords =
    {
        "prefixItems", "allOf", "anyOf", "oneOf"
    };
}
