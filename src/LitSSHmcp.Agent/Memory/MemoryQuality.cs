using System.Text;
using System.Text.RegularExpressions;
using LitSSHmcp.Core.Services.Security;

namespace LitSSHmcp.Agent;

/// <summary>
/// 记忆质量门：敏感信息检测（凭据不入库）与会话片段压缩（日志轮只留要点）。
/// </summary>
public static partial class MemoryQuality
{
    /// <summary>raw 单条片段最大字符数（超出由 IndexTurnAsync 截断前压缩）。</summary>
    public const int MaxRawChars = 4000;

    /// <summary>日志轮压缩后的目标最大字符数。</summary>
    public const int MaxCompressedLogChars = 200;

    [GeneratedRegex(
        @"(?i)(password|passwd|pwd|secret|api[_-]?key|access[_-]?key|private[_-]?key|token|bearer|authorization)\s*[:=]\s*\S{4,}")]
    private static partial Regex AssignmentSecret();

    [GeneratedRegex(@"-----BEGIN (RSA |OPENSSH |EC |DSA |PGP )?PRIVATE KEY-----")]
    private static partial Regex PemPrivateKey();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b")]                     // AWS Access Key
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(@"\bghp_[A-Za-z0-9]{36}\b|\bgithub_pat_[A-Za-z0-9_]{22,}\b")]   // GitHub token
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"sk-[A-Za-z0-9_-]{20,}")]                    // OpenAI/通用 secret key 形态
    private static partial Regex SkKey();

    /// <summary>content/key 是否疑似含凭据（密码/密钥/令牌明文）。</summary>
    public static bool ContainsSensitive(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (AssignmentSecret().IsMatch(text) || PemPrivateKey().IsMatch(text)
            || AwsAccessKey().IsMatch(text) || GitHubToken().IsMatch(text) || SkKey().IsMatch(text));

    /// <summary>
    /// 构造入 raw 记忆的片段：日志型超长输入压缩为「要点+结论」（≤200 字）；
    /// 普通对话也**不再整段保存助手回答**——表格、端口、在线状态等会被当成可复用结论注入，
    /// 而它们只是当时的快照。改为「问题 + 证据路径 + 结论摘要」，并给事实型值打 volatile 标记。
    /// </summary>
    public static string? BuildRawContent(string userText, string assistantText)
    {
        userText = (userText ?? string.Empty).Trim();
        assistantText = (assistantText ?? string.Empty).Trim();
        if (userText.Length == 0 && assistantText.Length == 0)
            return null;
        if (userText.Length + assistantText.Length > MaxRawChars || LooksLikeLogDump(userText))
            return CompressLogTurn(userText, assistantText);

        var sb = new StringBuilder();
        // 显式 \n（不用 AppendLine）：保持跨平台一致，也便于测试与下游工具稳定断言。
        sb.Append("[线索·非结论] 问题: ").Append(TruncateLine(StripFacts(userText), 120)).Append('\n');
        var clues = ExtractClues(userText + "\n" + assistantText);
        if (clues.Length > 0)
            sb.Append("证据路径: ").Append(clues).Append('\n');
        var conclusion = TruncateLine(StripFacts(assistantText), RawConclusionChars);
        sb.Append("结论摘要: ").Append(conclusion);
        if (LooksVolatile(conclusion))
            sb.Append(" [volatile: 端口/路径/版本/在线状态等事实值随时间变化, 必须先在目标机核实]");
        return sb.ToString();
    }

    /// <summary>结论摘要保留的字符数（完整回答不再入库）。</summary>
    public const int RawConclusionChars = 140;

    /// <summary>含 IP / 端口 / 版本号 / 在线状态等易过期事实值的结论。</summary>
    private static readonly Regex VolatileFactPattern = new(
        @"\b\d{1,3}(?:\.\d{1,3}){3}\b|:\d{2,5}\b|\bv?\d+\.\d+(?:\.\d+)?\b|在线|离线|运行中|已停止|监听|版本|端口",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static bool LooksVolatile(string text) => VolatileFactPattern.IsMatch(text);

    /// <summary>去掉 Markdown 表格行与代码块围栏，避免把整张事实表当结论注入。</summary>
    private static string StripFacts(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var kept = text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('|') && !line.StartsWith("```", StringComparison.Ordinal));
        return string.Join(" ", kept);
    }

    /// <summary>
    /// 构造可入库的 raw 片段：先按日志/长度压缩，再做持久化前脱敏；脱敏后仍疑似含凭据则整轮丢弃。
    /// raw 与结构化记忆共用「凭据绝不入库」原则。
    /// </summary>
    public static string? PrepareRawContent(string userText, string assistantText)
    {
        var content = BuildRawContent(userText, assistantText);
        if (content is null)
            return null;

        var (redacted, _) = SecretRedactor.RedactWithChange(content);
        if (string.IsNullOrWhiteSpace(redacted) || ContainsSensitive(redacted))
            return null;
        return redacted;
    }

    /// <summary>用户输入是否像"贴了一大段日志/命令原始输出"（多行 + 时间戳/常见日志前缀密度高）。</summary>
    public static bool LooksLikeLogDump(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var lines = text.Split('\n');
        if (lines.Length < 8)
            return false;
        var logLines = lines.Count(l =>
            LogLine().IsMatch(l) || l.Contains(" at ", StringComparison.OrdinalIgnoreCase)
            || l.Contains("INFO", StringComparison.Ordinal) || l.Contains("ERROR", StringComparison.Ordinal)
            || l.Contains("WARN", StringComparison.Ordinal) || l.Contains("Exception", StringComparison.Ordinal));
        return logLines >= Math.Max(5, lines.Length / 2);
    }

    [GeneratedRegex(@"^\s*\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}")]
    private static partial Regex LogLine();

    /// <summary>把日志轮压缩为「用户要点(路径/应用线索) + 助手结论」≤200 字。</summary>
    public static string CompressLogTurn(string userText, string assistantText)
    {
        var clues = ExtractClues(userText);
        var conclusion = TruncateLine(assistantText, 120);
        var text = (clues.Length > 0 ? clues + " | " : "") + conclusion;
        return TruncateLine(text, MaxCompressedLogChars);
    }

    /// <summary>从大段文本中抽取路径/主机/应用名等可复用线索（最多 3 条, 每条 ≤60 字）。</summary>
    public static string ExtractClues(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var clues = new List<string>();
        foreach (Match m in PathLike().Matches(text))
        {
            var v = m.Value.TrimEnd('，', '。', ',', ';', '）', ')');
            if (v.Length >= 4 && !clues.Contains(v, StringComparer.Ordinal))
                clues.Add(v);
            if (clues.Count >= 3)
                break;
        }
        return string.Join("; ", clues.Select(c => TruncateLine(c, 60)));
    }

    [GeneratedRegex(@"/[\w.\-/]{4,}|[A-Za-z]:\\[\w.\-\\]{4,}")]
    private static partial Regex PathLike();

    private static string TruncateLine(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var flat = text.Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
