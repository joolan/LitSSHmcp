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
    /// 构造入 raw 记忆的片段：日志型超长用户输入压缩为「应用/路径要点+助手结论」（≤200 字），
    /// 普通对话截断到 <see cref="MaxRawChars"/>；返回 null 表示该轮不值得入库。
    /// </summary>
    public static string? BuildRawContent(string userText, string assistantText)
    {
        userText = (userText ?? string.Empty).Trim();
        assistantText = (assistantText ?? string.Empty).Trim();
        if (userText.Length == 0 && assistantText.Length == 0)
            return null;
        if (userText.Length + assistantText.Length > MaxRawChars || LooksLikeLogDump(userText))
        {
            var compressed = CompressLogTurn(userText, assistantText);
            return compressed;
        }
        return $"用户: {userText}\n助手: {assistantText}";
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
