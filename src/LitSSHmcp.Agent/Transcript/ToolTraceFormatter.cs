using System.Text;

namespace LitSSHmcp.Agent.Transcript;

/// <summary>一条工具轨迹（与落库 <c>role="tool"</c> 行一一对应）。</summary>
public readonly record struct ToolTraceItem(string Tool, string? Args, string? Result, bool Ok, long Ms);

/// <summary>
/// 工具轨迹的历史恢复文本格式（桌面与 Web 共用，保证两端喂给模型的上下文一致、前缀缓存可复用）。
/// 不伪造 function_call 配对，只把"本轮做过什么"作为 System 证据。
/// </summary>
public static class ToolTraceFormatter
{
    /// <summary>轨迹段前缀（桌面历史恢复与 Web 重建共用同一常量）。</summary>
    public const string Prefix = "【本轮工具轨迹（历史恢复，仅供参考；以实际工具结果为准）】\n";

    private const int TraceArgChars = 300;
    private const int TraceResultChars = 600;
    private const int TraceTurnMaxChars = 3000;

    public static string Build(IEnumerable<ToolTraceItem> steps)
    {
        var sb = new StringBuilder();
        foreach (var step in steps)
        {
            if (sb.Length >= TraceTurnMaxChars)
                break;
            var args = Truncate(step.Args, TraceArgChars);
            var result = Truncate(step.Result, TraceResultChars);
            sb.Append("- ").Append(step.Tool)
              .Append(args.Length > 0 ? $"({args})" : "()")
              .Append(step.Ok ? " → 成功" : " → 失败")
              .Append($"({step.Ms}ms)");
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
