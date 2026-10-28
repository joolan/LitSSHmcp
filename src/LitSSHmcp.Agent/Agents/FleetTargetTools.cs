using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>批量变更目标选择：返回当前登录用户可见的 SSH 服务器清单（只读）。</summary>
public static class FleetTargetTools
{
    public const string ToolName = "fleet_targets";

    public static IAgentTool? Create(IAgentTool? listServersTool, Func<string, bool>? isDenied = null)
    {
        if (listServersTool is null)
            return null;

        var function = new LocalFunction(
            ToolName,
            "列出当前会话可见的 SSH 服务器（id/name/host/tags），用于批量变更目标选择；支持 query 按 id/名称/主机/标签过滤。只读。",
            """
            {"type":"object","properties":{
              "query":{"type":"string","description":"可选过滤词：匹配 id/名称/主机/标签/描述"}
            }}
            """,
            async (args, ct) =>
            {
                if (isDenied?.Invoke(listServersTool.Name) == true)
                    return "错误: ssh_list_servers 已被当前会话策略禁用，无法加载目标清单";
                var result = await listServersTool.InvokeAsync(new Dictionary<string, object?>(), ct);
                var json = ToJson(result);
                if (!TryParse(json, out var root) ||
                    !TryGetProperty(root, "servers", out var servers) ||
                    servers.ValueKind != JsonValueKind.Array)
                    return json;

                var query = (ArgumentReader.ReadString(args, "query") ?? string.Empty).Trim();
                var items = new List<JsonElement>();
                foreach (var server in servers.EnumerateArray())
                {
                    if (query.Length == 0 || Matches(server, query))
                        items.Add(server.Clone());
                }
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    count = items.Count,
                    servers = items
                });
            });

        return new LocalAgentTool(function, readOnly: true);
    }

    private static bool Matches(JsonElement server, string query)
    {
        foreach (var key in new[] { "id", "name", "host", "description" })
        {
            if (TryGetProperty(server, key, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                value.GetString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }
        if (TryGetProperty(server, "tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tags.EnumerateArray())
            {
                if (tag.ValueKind == JsonValueKind.String &&
                    tag.GetString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                    return true;
            }
        }
        return false;
    }

    private static string ToJson(object? value)
    {
        if (value is null) return "{}";
        if (value is JsonElement element) return Unwrap(element).GetRawText();
        if (value is string text) return text;
        return JsonSerializer.Serialize(value);
    }

    private static JsonElement Unwrap(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            TryGetProperty(element, "structuredContent", out var structured))
            return Unwrap(structured);
        if (element.ValueKind == JsonValueKind.Object &&
            TryGetProperty(element, "result", out var result) && result.ValueKind != JsonValueKind.Null)
            return Unwrap(result);
        return element;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            value = property.Value;
            return true;
        }
        return false;
    }

    private static bool TryParse(string json, out JsonElement root)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            root = default;
            return false;
        }
    }
}
