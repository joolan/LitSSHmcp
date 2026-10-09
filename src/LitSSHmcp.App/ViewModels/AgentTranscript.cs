using System.Text;
using System.Text.Json;

namespace LitSSHmcp.App.ViewModels;

/// <summary>
/// 会话记录中工具轨迹的序列化/反序列化与文本拼装（唯一实现，供持久化与加载共用）。
/// 落库形态为 <c>agent_messages</c> 的 <c>role="tool"</c> 行，内容为紧凑 JSON。
/// </summary>
public static class AgentTranscript
{
    /// <summary>工具步骤的落库形态。</summary>
    public sealed record Step(string Tool, string? Args, string? Result, bool Ok, long Ms);

    /// <summary>结果落库最大字符数（超出截断）。</summary>
    public const int ResultMaxChars = 20000;

    private const int TraceArgChars = 300;
    private const int TraceResultChars = 600;
    private const int TraceTurnMaxChars = 3000;

    /// <summary>把界面步骤序列化为落库 JSON（结果按 <see cref="ResultMaxChars"/> 截断）。</summary>
    public static string Serialize(AgentStep step)
        => JsonSerializer.Serialize(new Step(
            step.Tool, step.ArgsJson, Truncate(step.Result, ResultMaxChars), step.Success, step.DurationMs));

    /// <summary>解析落库 JSON；损坏时返回 null（跳过该行，不影响其余恢复）。</summary>
    public static Step? Deserialize(string content)
    {
        try { return JsonSerializer.Deserialize<Step>(content); }
        catch { return null; }
    }

    /// <summary>把界面步骤还原为可展示/可重试的 <see cref="AgentStep"/>。</summary>
    public static AgentStep ToStep(Step step)
    {
        var vm = new AgentStep(step.Tool, step.Args ?? string.Empty);
        vm.Complete(step.Ok, step.Result ?? string.Empty, step.Ms);
        return vm;
    }

    /// <summary>
    /// 把一轮的工具步骤拼成紧凑文本轨迹（每轮总量上限 <see cref="TraceTurnMaxChars"/>）；用于重启恢复后
    /// 让模型看到"本轮做过什么"的证据，且不伪造 function_call 配对。
    /// </summary>
    public static string BuildTrace(IEnumerable<AgentStep> steps)
    {
        var sb = new StringBuilder();
        foreach (var step in steps)
        {
            if (sb.Length >= TraceTurnMaxChars)
                break;
            var args = Truncate(step.ArgsJson, TraceArgChars);
            var result = Truncate(step.Result, TraceResultChars);
            sb.Append("- ").Append(step.Tool)
              .Append(args.Length > 0 ? $"({args})" : "()")
              .Append(step.Success ? " → 成功" : " → 失败")
              .Append($"({step.DurationMs}ms)");
            if (result.Length > 0)
                sb.Append(": ").Append(result.Replace("\r", " ").Replace("\n", " "));
            sb.AppendLine();
        }
        var text = sb.ToString().TrimEnd();
        return text.Length <= TraceTurnMaxChars ? text : text[..TraceTurnMaxChars] + " …(轨迹过长已截断)";
    }

    private static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty : (text.Length <= max ? text : text[..max] + " …(截断)");
}
