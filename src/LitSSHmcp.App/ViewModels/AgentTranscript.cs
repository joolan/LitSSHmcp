using System.Text;
using System.Text.Encodings.Web;
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

    /// <summary>
    /// 落库用 JSON 选项：中文/引号不转义（默认编码器会把内层 JSON 引号写成 <c>\u0022</c>、
    /// 中文写成 <c>\uXXXX</c>，导致 Web 历史回显成乱码般的转义串）。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>结果落库最大字符数（超出截断）。</summary>
    public const int ResultMaxChars = 20000;

    private const int TraceArgChars = 300;
    private const int TraceResultChars = 600;
    private const int TraceTurnMaxChars = 3000;

    /// <summary>把界面步骤序列化为落库 JSON（结果按 <see cref="ResultMaxChars"/> 截断）。</summary>
    public static string Serialize(AgentStep step)
        => JsonSerializer.Serialize(new Step(
            step.Tool, step.ArgsJson, Truncate(step.Result, ResultMaxChars), step.Success, step.DurationMs), JsonOptions);

    /// <summary>解析落库 JSON；损坏时返回 null（跳过该行，不影响其余恢复）。</summary>
    public static Step? Deserialize(string content)
    {
        try { return JsonSerializer.Deserialize<Step>(content, JsonOptions); }
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
    /// 让模型看到"本轮做过什么"的证据，且不伪造 function_call 配对。信息步骤（<see cref="AgentStep.IsInfo"/>，如记忆召回）不计入。
    /// </summary>
    public static string BuildTrace(IEnumerable<AgentStep> steps)
        => LitSSHmcp.Agent.Transcript.ToolTraceFormatter.Build(
            steps.Where(s => !s.IsInfo)
                 .Select(s => new LitSSHmcp.Agent.Transcript.ToolTraceItem(
                     s.Tool, s.ArgsJson, s.Result, s.Success, s.DurationMs)));

    private static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty : (text.Length <= max ? text : text[..max] + " …(截断)");
}
