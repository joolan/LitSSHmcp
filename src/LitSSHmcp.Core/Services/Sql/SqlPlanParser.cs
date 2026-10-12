using System.Text.Json;

namespace LitSSHmcp.Core.Services.Sql;

/// <summary>执行计划树的一个节点（用于可视化展示）。</summary>
public sealed class SqlPlanNode
{
    public string Name { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public IReadOnlyList<SqlPlanNode> Children { get; init; } = Array.Empty<SqlPlanNode>();
}

/// <summary>
/// 解析 PostgreSQL <c>EXPLAIN (FORMAT JSON)</c> 的输出为计划树。
/// 输入形如 <c>[{ "Plan": { "Node Type": "Seq Scan", ..., "Plans": [ ... ] } }]</c>。
/// </summary>
public static class SqlPlanParser
{
    public static IReadOnlyList<SqlPlanNode> ParsePostgresJson(string? json)
    {
        var list = new List<SqlPlanNode>();
        if (string.IsNullOrWhiteSpace(json))
            return list;

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement plan;
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                    return list;
                var first = root[0];
                plan = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("Plan", out var p) ? p : first;
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Plan", out var p2))
            {
                plan = p2;
            }
            else
            {
                plan = root;
            }

            if (plan.ValueKind == JsonValueKind.Object)
                list.Add(Build(plan));
        }
        catch
        {
            // 非法 JSON：返回空
        }
        return list;
    }

    private static SqlPlanNode Build(JsonElement node)
    {
        string Get(string key) => node.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.ToString()
            : string.Empty;

        var type = Get("Node Type");
        var relation = Get("Relation Name");
        var index = Get("Index Name");
        var subplan = Get("Subplan Name");
        var cost = Get("Total Cost");
        var rows = Get("Plan Rows");
        var actual = Get("Actual Total Time");

        var name = type;
        if (!string.IsNullOrEmpty(relation))
            name += "  " + relation;
        if (!string.IsNullOrEmpty(index))
            name += " [" + index + "]";
        if (!string.IsNullOrEmpty(subplan))
            name = subplan + " → " + name;

        var detail = $"cost={cost}  rows={rows}";
        if (!string.IsNullOrEmpty(actual))
            detail += $"  actual={actual}ms";

        var children = new List<SqlPlanNode>();
        if (node.TryGetProperty("Plans", out var plans) && plans.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in plans.EnumerateArray())
                if (child.ValueKind == JsonValueKind.Object)
                    children.Add(Build(child));
        }

        return new SqlPlanNode { Name = name, Detail = detail, Children = children };
    }
}
