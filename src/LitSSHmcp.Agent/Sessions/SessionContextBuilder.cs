using System.Text.Json;
using LitSSHmcp.Agent.Transcript;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Topology;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent.Sessions;

/// <summary>冷启动会话上下文（模型历史 + 系统提示要素），桌面/Web 共用同一构建逻辑。</summary>
public sealed record SessionContextBuild(
    IReadOnlyList<ChatMessage> History,
    AgentPromptContext? ActiveAgent,
    IReadOnlyCollection<string>? ActiveSkillIds,
    bool PlanMode,
    string? AssetGraph);

/// <summary>
/// 从落库消息重建"冷启动"上下文：与桌面 AgentViewModel.BuildTranscriptAsync 对齐——
/// 只注入最近 <c>agent.coldStartRecentTurns</c> 轮；每轮带工具轨迹 System 证据；
/// 视觉模型重载最近 3 张图片；系统提示要素（智能体/技能/计划模式/资产图谱）一并返回。
/// 对齐目的：Web 与桌面喂给模型的上下文一致，切换端时前缀缓存尽可能复用。
/// </summary>
public static class SessionContextBuilder
{
    private const int ReloadImageQuota = 3;

    private sealed class Turn(string text)
    {
        public string Text { get; } = text;
        public bool HasUser { get; set; }
        public List<string> Images { get; } = new();
        public List<ToolTraceItem> Steps { get; } = new();
        public string Assistant { get; set; } = string.Empty;
    }

    public static SessionContextBuild Build(AppConfig config, string? agentId, string? activeSkillsJson,
        bool planMode, IReadOnlyList<AgentMessageRow> messages, string? workspaceDir, bool supportsVision)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(messages);

        var turns = BuildTurns(messages);
        var allowedImages = supportsVision ? PickRecentImages(turns) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var recentTurns = config.Agent.ColdStartRecentTurns;
        var from = recentTurns > 0 ? Math.Max(0, turns.Count - recentTurns) : 0;

        var history = new List<ChatMessage>();
        if (from > 0)
        {
            history.Add(new ChatMessage(ChatRole.System,
                $"（本会话较早的 {from} 轮对话已省略以降低冷启动上下文开销；如需关键信息请让用户补充。）"));
        }

        for (var i = from; i < turns.Count; i++)
        {
            var turn = turns[i];
            if (turn.HasUser)
            {
                var contents = new List<AIContent>();
                if (!string.IsNullOrEmpty(turn.Text))
                    contents.Add(new TextContent(turn.Text));
                if (supportsVision)
                {
                    foreach (var image in turn.Images)
                    {
                        if (!allowedImages.Contains(image))
                            continue;
                        var full = ResolveImage(workspaceDir, image);
                        if (full is null)
                            continue;
                        try
                        {
                            contents.Add(new DataContent(File.ReadAllBytes(full), MediaTypeOf(full)));
                        }
                        catch
                        {
                            // 单张图片读取失败忽略，仅保留文本
                        }
                    }
                }
                if (contents.Count == 0)
                    contents.Add(new TextContent(string.Empty));
                history.Add(new ChatMessage(ChatRole.User, contents));
            }

            var trace = ToolTraceFormatter.Build(turn.Steps);
            if (trace.Length > 0)
                history.Add(new ChatMessage(ChatRole.System, ToolTraceFormatter.Prefix + trace));

            if (!string.IsNullOrEmpty(turn.Assistant))
                history.Add(new ChatMessage(ChatRole.Assistant, turn.Assistant));
        }

        return new SessionContextBuild(
            history,
            ResolveAgent(config, agentId),
            ParseSkills(activeSkillsJson),
            planMode,
            TryFormatAssetGraph(config));
    }

    /// <summary>按会话模型解析是否支持视觉（与运行时选择同一回退顺序）。</summary>
    public static bool ResolveSupportsVision(AppConfig config, string? providerId)
    {
        var flat = AgentProviderGroupConfig.Flatten(config.Agent.Providers);
        var provider =
            (!string.IsNullOrWhiteSpace(providerId) ? flat.FirstOrDefault(p => p.Id == providerId) : null)
            ?? flat.FirstOrDefault(p => p.Id == config.Agent.ActiveProviderId)
            ?? flat.FirstOrDefault();
        return provider?.SupportsVision == true;
    }

    private static List<Turn> BuildTurns(IReadOnlyList<AgentMessageRow> messages)
    {
        var turns = new List<Turn>();
        Turn? current = null;

        foreach (var row in messages)
        {
            switch (row.Role)
            {
                case "user":
                {
                    var (text, images) = ParseUserContent(row.Content);
                    current = new Turn(text) { HasUser = true };
                    current.Images.AddRange(images);
                    turns.Add(current);
                    break;
                }
                case "tool":
                {
                    var step = ParseToolRow(row.Content);
                    if (step is null)
                        break;
                    if (current is null)
                    {
                        current = new Turn(string.Empty);
                        turns.Add(current);
                    }
                    current.Steps.Add(step.Value);
                    break;
                }
                case "assistant":
                {
                    if (current is null)
                    {
                        current = new Turn(string.Empty);
                        turns.Add(current);
                    }
                    current.Assistant = row.Content;
                    break;
                }
            }
        }

        return turns;
    }

    private static HashSet<string> PickRecentImages(IReadOnlyList<Turn> turns)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        for (var i = turns.Count - 1; i >= 0 && count < ReloadImageQuota; i--)
        {
            foreach (var image in turns[i].Images)
            {
                if (count >= ReloadImageQuota)
                    break;
                if (allowed.Add(image))
                    count++;
            }
        }
        return allowed;
    }

    private static (string Text, List<string> Images) ParseUserContent(string content)
    {
        var images = new List<string>();
        var lines = new List<string>();
        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("🖼"))
            {
                var open = line.IndexOf('[');
                var close = line.LastIndexOf(']');
                if (open >= 0 && close > open)
                    images.Add(line[(open + 1)..close].Trim());
                continue;
            }
            lines.Add(raw);
        }
        return (string.Join("\n", lines).Trim(), images);
    }

    private static ToolTraceItem? ParseToolRow(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string? GetString(string pascal, string camel) =>
                root.TryGetProperty(pascal, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : root.TryGetProperty(camel, out var fallback) && fallback.ValueKind == JsonValueKind.String
                        ? fallback.GetString()
                        : null;

            var tool = GetString("Tool", "tool");
            if (string.IsNullOrWhiteSpace(tool))
                return null;

            var ok = true;
            if (root.TryGetProperty("Ok", out var okElement) || root.TryGetProperty("ok", out okElement))
            {
                if (okElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    ok = okElement.GetBoolean();
            }

            long ms = 0;
            if ((root.TryGetProperty("Ms", out var msElement) || root.TryGetProperty("ms", out msElement))
                && msElement.TryGetInt64(out var parsedMs))
            {
                ms = parsedMs;
            }

            return new ToolTraceItem(tool!, GetString("Args", "args"), GetString("Result", "result"), ok, ms);
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveImage(string? workspaceDir, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(workspaceDir) || string.IsNullOrWhiteSpace(relativePath))
            return null;
        try
        {
            var full = Path.GetFullPath(Path.Combine(workspaceDir, relativePath));
            return PathGuard.IsUnder(workspaceDir, full) && File.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    private static string MediaTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/png"
    };

    private static AgentPromptContext? ResolveAgent(AppConfig config, string? agentId)
    {
        var agents = config.Agent.Agents ?? Array.Empty<ChatAgentConfig>();
        var agent = agents.FirstOrDefault(a => string.Equals(a.Id, agentId, StringComparison.Ordinal))
                    ?? agents.FirstOrDefault(a => string.Equals(a.Id, config.Agent.ActiveAgentId, StringComparison.Ordinal))
                    ?? agents.FirstOrDefault();
        return agent?.ToPromptContext();
    }

    private static IReadOnlyCollection<string>? ParseSkills(string? activeSkillsJson)
    {
        if (string.IsNullOrWhiteSpace(activeSkillsJson))
            return null;
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(activeSkillsJson);
            return list is { Count: > 0 } ? list : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryFormatAssetGraph(AppConfig config)
    {
        try
        {
            return AssetGraphPromptFormatter.Format(config);
        }
        catch
        {
            return null;
        }
    }
}
