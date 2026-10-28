using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LitSSHmcp.Agent;

/// <summary>
/// 按当前会话实际可用的工具裁剪 <c>mcp_usage_guide</c> 的 JSON 结果：
/// 删除引用未暴露工具的路由行/子项，避免 profile/只读/分组裁剪后仍推荐不可用工具。
/// 非 JSON 或解析失败时原样返回。
/// </summary>
public static class GuideResultFilter
{
    private static readonly Regex ToolToken = new("[a-z][a-z0-9_]{2,}", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public static string? Filter(string? json, IReadOnlySet<string> allToolNames, IReadOnlySet<string> allowedToolNames)
    {
        if (string.IsNullOrWhiteSpace(json) || allToolNames.Count == 0)
            return json;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }

        if (root is not JsonObject && root is not JsonArray)
            return json;

        Prune(root, allToolNames, allowedToolNames);
        return root.ToJsonString(OutputOptions);
    }

    private static void Prune(JsonNode node, IReadOnlySet<string> all, IReadOnlySet<string> allowed)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["tools"] is JsonArray tools)
                {
                    for (var i = tools.Count - 1; i >= 0; i--)
                    {
                        var name = (tools[i] as JsonObject)?["name"]?.GetValue<string>();
                        if (name is null || !allowed.Contains(name))
                            tools.RemoveAt(i);
                    }
                }

                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (key == "tools")
                        continue;

                    var value = obj[key];
                    if (value is JsonValue textValue && textValue.TryGetValue<string>(out var text))
                    {
                        if (key == "error_handling")
                        {
                            var parts = text.Split("; ", StringSplitOptions.RemoveEmptyEntries)
                                .Where(p => !ShouldDropText(p, all, allowed))
                                .ToArray();
                            if (parts.Length == 0)
                                obj.Remove(key);
                            else
                                obj[key] = string.Join("; ", parts);
                        }
                        else if (ShouldDropText(text, all, allowed))
                        {
                            obj.Remove(key);
                        }
                    }
                    else
                    {
                        Prune(value!, all, allowed);
                    }
                }

                break;

            case JsonArray array:
                for (var i = array.Count - 1; i >= 0; i--)
                {
                    var item = array[i];
                    if (item is JsonValue textValue && textValue.TryGetValue<string>(out var text))
                    {
                        if (ShouldDropText(text, all, allowed))
                            array.RemoveAt(i);
                    }
                    else
                    {
                        Prune(item!, all, allowed);
                    }
                }

                break;
        }
    }

    private static bool ShouldDropText(string text, IReadOnlySet<string> all, IReadOnlySet<string> allowed)
    {
        var referenced = ToolToken.Matches(text)
            .Select(m => m.Value)
            .Where(all.Contains)
            .Distinct(StringComparer.Ordinal);

        return referenced.Any(name => !allowed.Contains(name));
    }
}
