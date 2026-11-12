using System.Text;
using System.Text.Json;

namespace LitSSHmcp.Agent;

/// <summary>按工具类型给工具结果分配上下文预算；超限先摘要，再落盘。</summary>
internal static class ToolResultBudget
{
    public const int SnapshotChars = 8000;
    public const int LogChars = 8000;
    public const int FleetChars = 6000;
    public const int TopologyChars = 6000;

    public static int Resolve(string toolName, int configuredMaxChars)
    {
        var category = toolName switch
        {
            _ when toolName.StartsWith("ssh_snapshot_", StringComparison.OrdinalIgnoreCase) => SnapshotChars,
            "log_query" => LogChars,
            _ when toolName.EndsWith("_logs", StringComparison.OrdinalIgnoreCase) => LogChars,
            _ when toolName.StartsWith("fleet_", StringComparison.OrdinalIgnoreCase) => FleetChars,
            _ when toolName.StartsWith("topology_", StringComparison.OrdinalIgnoreCase) => TopologyChars,
            _ => 0
        };

        if (category == 0)
            return configuredMaxChars;
        return configuredMaxChars <= 0 ? category : Math.Max(configuredMaxChars, category);
    }

    public static bool PreferTail(string toolName) =>
        toolName.Equals("log_query", StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("docker_query", StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("local_shell", StringComparison.OrdinalIgnoreCase) ||
        toolName.EndsWith("_logs", StringComparison.OrdinalIgnoreCase);
}

/// <summary>工具结果超预算时生成紧凑摘要；原始结果仍由 SpillStore 保留。</summary>
internal static class ToolResultSummaryBuilder
{
    public static string Build(string toolName, string resultText, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(resultText) || maxChars <= 0)
            return string.Empty;

        var trimmed = resultText.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(resultText);
                var summary = SummarizeJson(doc.RootElement);
                if (!string.IsNullOrWhiteSpace(summary))
                    return Truncate(summary, maxChars);
            }
            catch (JsonException)
            {
                // 非 JSON 文本按普通文本处理
            }
        }

        var preferTail = ToolResultBudget.PreferTail(toolName);
        var textSummary = SummarizeText(resultText, preferTail, maxChars);
        return preferTail ? textSummary : Truncate(textSummary, maxChars);
    }

    private static string SummarizeJson(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (TryGet(root, "data", out var data) && TryGet(data, "sections", out var sections) &&
                sections.ValueKind == JsonValueKind.Object ||
                TryGet(root, "sections", out sections) && sections.ValueKind == JsonValueKind.Object)
            {
                var parts = new List<string>();
                foreach (var section in sections.EnumerateObject())
                {
                    if (section.Value.ValueKind != JsonValueKind.Object)
                        continue;
                    var status = TryGet(section.Value, "status", out var s) ? s.ToString() : "?";
                    var chars = TryGet(section.Value, "dataChars", out var c) ? c.ToString() : "?";
                    parts.Add($"{section.Name}({status},{chars})");
                }
                if (parts.Count > 0)
                    return "[快照摘要] sections: " + string.Join("; ", parts);
            }

            if (TryGet(root, "results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                var success = results.EnumerateArray().Count(item =>
                    TryGet(item, "success", out var ok) && ok.ValueKind == JsonValueKind.True);
                return $"[批量结果] total={results.GetArrayLength()}, success={success}, failed={results.GetArrayLength() - success}";
            }

            var meta = new List<string>();
            foreach (var key in new[] { "status", "state", "success", "error", "reason", "hint", "summary" })
            {
                if (TryGet(root, key, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                    meta.Add(key + "=" + Truncate(value.ToString(), 200));
            }
            if (meta.Count > 0)
                return "[JSON摘要] " + string.Join("; ", meta);
        }

        if (root.ValueKind == JsonValueKind.Array)
            return $"[数组结果] count={root.GetArrayLength()}";

        return string.Empty;
    }

    private static string SummarizeText(string text, bool preferTail, int maxChars)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var nonEmpty = lines.Count(l => l.Length > 0);
        var budget = Math.Max(40, maxChars - 40);
        if (preferTail)
        {
            var tail = text.Length > budget ? text[^budget..] : text;
            return $"[文本摘要] 约 {nonEmpty} 行，最新片段:\n{tail}";
        }

        var head = lines.Take(8).Select(l => Truncate(l, 180));
        return $"[文本摘要] 约 {nonEmpty} 行，开头片段:\n" + string.Join('\n', head);
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        return element.TryGetProperty(name, out value);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
