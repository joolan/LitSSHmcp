using System.Globalization;
using System.Text;
using System.Text.Json;
using LitSSHmcp.Agent.Sessions;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 事件/会话时间线：把用户请求、工具调用/结果、助手结论串成可回放的证据链，
/// 用于复盘、交接和审计；可选附带事件记录（现象/根因/处置/影响/回滚）。
/// </summary>
public static class TimelineTools
{
    public const string ToolName = "ops_timeline";

    public static IAgentTool Create(IAgentMemoryStore? incidentStore, IContextStore? contextStore = null)
    {
        var function = new LocalFunction(
            ToolName,
            "查看事件/会话时间线：用户请求 → 工具调用/结果 → 助手结论，可选附带事件记录；用于复盘/交接/审计。incidentId 省略时使用当前会话。",
            """{"type":"object","properties":{"incidentId":{"type":"integer","description":"可选：事件ID；省略时查看当前会话"},"sessionId":{"type":"string","description":"可选：会话ID（数字或 agent-数字）"},"limit":{"type":"integer","description":"最多消息数, 默认200, 上限500"}},"required":[]}""",
            async (args, ct) =>
            {
                var incidentId = ArgumentReader.ReadInt(args, "incidentId");
                var limit = Math.Clamp(ArgumentReader.ReadInt(args, "limit") ?? 200, 1, 500);
                IncidentRecord? incident = null;
                var sessionId = ArgumentReader.ReadString(args, "sessionId");
                if (incidentId is > 0 && incidentStore is not null)
                {
                    incident = await incidentStore.GetIncidentAsync(incidentId.Value, ct);
                    if (incident is null)
                        return $"(事件 #{incidentId} 不存在)";
                    if (string.IsNullOrWhiteSpace(sessionId))
                        sessionId = incident.SessionId;
                }

                var numericSession = NormalizeSessionId(sessionId) ?? NormalizeSessionId(LocalAudit.AmbientSessionId);
                List<AgentMessageRow> rows = new();
                if (numericSession is not null)
                {
                    try
                    {
                        var context = contextStore ?? new ContextStore();
                        rows = await context.GetMessagesAsync(numericSession.Value, ct);
                    }
                    catch
                    {
                        // 会话库不可读时仍返回事件记录
                    }
                }

                var sb = new StringBuilder();
                if (incident is not null)
                {
                    AppendIncident(sb, incident);
                }
                else if (rows.Count == 0)
                {
                    return string.IsNullOrWhiteSpace(sessionId)
                        ? "(未找到当前会话；请传 incidentId 或 sessionId)"
                        : $"(会话 {sessionId} 无消息记录，且未指定 incidentId)";
                }

                if (rows.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"## 时间线 (session {numericSession}, {rows.Count} 条)");
                    var start = Math.Max(0, rows.Count - limit);
                    if (start > 0)
                        sb.AppendLine($"（较早 {start} 条已省略，只显示最近 {rows.Count - start} 条）");
                    for (var i = start; i < rows.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var row = rows[i];
                        var time = FormatTime(row.Timestamp);
                        switch (row.Role)
                        {
                            case "user":
                                sb.AppendLine($"[{time}] 用户: {Truncate(row.Content, 1200)}");
                                break;
                            case "assistant":
                                sb.AppendLine($"[{time}] 助手: {Truncate(row.Content, 1800)}");
                                break;
                            case "tool":
                                sb.AppendLine($"[{time}] {FormatTool(row.Content)}");
                                break;
                            case "reasoning":
                                // 思考内容默认不进入时间线（只保留可审计的动作/结论）
                                break;
                        }
                    }
                }

                return sb.ToString().Trim();
            });

        return new LocalAgentTool(function, readOnly: true);
    }

    private static void AppendIncident(StringBuilder sb, IncidentRecord incident)
    {
        sb.AppendLine($"## 事件 #{incident.Id} [{incident.Status}] {incident.Title}");
        if (!string.IsNullOrWhiteSpace(incident.AssetIds)) sb.AppendLine($"- 资产: {incident.AssetIds}");
        if (!string.IsNullOrWhiteSpace(incident.Symptom)) sb.AppendLine($"- 现象: {Truncate(incident.Symptom, 1500)}");
        if (!string.IsNullOrWhiteSpace(incident.Evidence)) sb.AppendLine($"- 证据: {Truncate(incident.Evidence, 1500)}");
        if (!string.IsNullOrWhiteSpace(incident.RootCause)) sb.AppendLine($"- 根因: {Truncate(incident.RootCause, 1500)}");
        if (!string.IsNullOrWhiteSpace(incident.Actions)) sb.AppendLine($"- 处置: {Truncate(incident.Actions, 2000)}");
        if (!string.IsNullOrWhiteSpace(incident.Outcome)) sb.AppendLine($"- 结果: {Truncate(incident.Outcome, 1000)}");
        sb.AppendLine($"- 置信度: {incident.Confidence:0.##} · 更新: {incident.UpdatedAt}");
        if (incident.SupersededBy is long next) sb.AppendLine($"- 已被事件 #{next} 取代");
    }

    private static string FormatTool(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            var tool = ReadString(root, "Tool") ?? "工具";
            var ok = root.TryGetProperty("Ok", out var okElement) &&
                     okElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                     okElement.GetBoolean();
            var ms = root.TryGetProperty("Ms", out var msElement) && msElement.TryGetInt64(out var value) ? value : 0;
            var info = root.TryGetProperty("IsInfo", out var infoElement) &&
                       infoElement.ValueKind == JsonValueKind.True;
            var args = ReadString(root, "Args");
            var result = ReadString(root, "Result");
            if (info)
                return $"信息 {tool}: {Truncate(result ?? string.Empty, 600)}";
            var sb = new StringBuilder();
            sb.Append("工具 ").Append(tool).Append(ok ? " 成功" : " 失败").Append($" ({ms}ms)");
            if (!string.IsNullOrWhiteSpace(args))
                sb.Append("\n  参数: ").Append(Truncate(args!, 500));
            if (!string.IsNullOrWhiteSpace(result))
                sb.Append("\n  结果: ").Append(Truncate(result!, 900));
            return sb.ToString();
        }
        catch
        {
            return "工具轨迹: " + Truncate(content, 900);
        }
    }

    private static long? NormalizeSessionId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim();
        if (text.StartsWith("agent-", StringComparison.OrdinalIgnoreCase))
            text = text[6..];
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : null;
    }

    private static string FormatTime(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToLocalTime().ToString("HH:mm:ss")
            : "-";

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
