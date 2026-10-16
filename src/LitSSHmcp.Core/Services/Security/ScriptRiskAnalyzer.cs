using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Security;

/// <summary>脚本风险等级：Blocked=硬拒绝(不进审批直接拒)，Suspicious=需人工审批并展示命中，Safe=无命中(仍走审批流程由调用方决定)。</summary>
public enum ScriptRiskLevel
{
    Safe,
    Suspicious,
    Blocked
}

/// <summary>一条脚本风险命中（行号 + 等级 + 规则 + 该行片段）。</summary>
public sealed record ScriptRiskFinding(int Line, ScriptRiskLevel Level, string Rule, string Excerpt);

/// <summary>脚本风险扫描报告：审批弹窗与工具返回共用的结构化结果。</summary>
public sealed record ScriptRiskReport
{
    public required string FileName { get; init; }
    public ScriptRiskLevel Level { get; init; } = ScriptRiskLevel.Safe;
    public IReadOnlyList<ScriptRiskFinding> Findings { get; init; } = Array.Empty<ScriptRiskFinding>();
    public int LineCount { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    /// <summary>内容是否超出扫描上限（超出部分未分析）。</summary>
    public bool Truncated { get; init; }
    /// <summary>额外说明（如"内容读取失败, 视为未审脚本"、"与上传时哈希不一致"）。</summary>
    public string? Note { get; init; }

    public string LevelText => Level switch
    {
        ScriptRiskLevel.Blocked => "危险(硬拒绝)",
        ScriptRiskLevel.Suspicious => "可疑(需人工确认)",
        _ => "未发现危险模式"
    };
}

/// <summary>
/// 脚本内容风险扫描（**服务端权威**）：以 security.commandFilter 的 Blocked/Sensitive 规则逐行扫描，
/// 另叠加脚本场景专用规则（下载即执行/写裸盘/反弹 shell/窃取 SSH 凭据/清历史/写 crontab 等）。
/// 永不信任脚本：本报告用于审批展示；命中 Blocked 由调用方直接硬拒，不进审批。
/// </summary>
public class ScriptRiskAnalyzer
{
    /// <summary>参与脚本扫描的扩展名。</summary>
    public static readonly string[] ScriptExtensions = { ".sh", ".bash", ".zsh", ".py", ".ps1", ".pl" };

    /// <summary>单次扫描的内容上限（约 256KB），超出置 Truncated。</summary>
    public const int MaxAnalyzeChars = 262_144;
    private const int MaxFindings = 40;
    private const int MaxExcerptChars = 160;

    private readonly ISecurityOptionsProvider _options;

    public ScriptRiskAnalyzer(ISecurityOptionsProvider options) => _options = options;

    public static bool IsScriptFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var ext = Path.GetExtension(path.Trim().TrimEnd('/'));
        return ScriptExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>扫描脚本内容；fileName 仅用于报告展示与扩展名判断（非脚本文件也允许显式扫描）。</summary>
    public ScriptRiskReport Analyze(string? fileName, string? content)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "script" : Path.GetFileName(fileName.Trim());
        if (string.IsNullOrEmpty(content))
        {
            return new ScriptRiskReport
            {
                FileName = name,
                Level = ScriptRiskLevel.Suspicious,
                LineCount = 0,
                Note = "内容为空或无法读取, 视为未审脚本"
            };
        }

        var truncated = content.Length > MaxAnalyzeChars;
        var body = truncated ? content[..MaxAnalyzeChars] : content;
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var config = _options.CommandFilter;

        var findings = new List<ScriptRiskFinding>();
        var level = ScriptRiskLevel.Safe;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (config.IsBlocked(line))
                Add(i + 1, ScriptRiskLevel.Blocked, "命令过滤:危险命令", line);
            else if (config.IsSensitive(line))
                Add(i + 1, ScriptRiskLevel.Suspicious, "命令过滤:敏感命令", line);

            foreach (var (ruleLevel, pattern, label) in EffectiveRules())
            {
                try
                {
                    if (Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase))
                        Add(i + 1, ruleLevel, label, line);
                }
                catch (ArgumentException)
                {
                    // 配置里的自定义正则非法: 跳过该条, 不影响其余规则扫描
                }
            }

            if (findings.Count >= MaxFindings)
                break;
        }

        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16].ToLowerInvariant();
        return new ScriptRiskReport
        {
            FileName = name,
            Level = level,
            Findings = findings,
            LineCount = lines.Length,
            Sha256 = sha,
            Truncated = truncated,
            Note = truncated ? $"内容超过扫描上限({MaxAnalyzeChars}字符), 超出部分未分析" : null
        };

        void Add(int lineNo, ScriptRiskLevel findingLevel, string rule, string excerpt)
        {
            if (findings.Count >= MaxFindings)
                return;
            findings.Add(new ScriptRiskFinding(lineNo, findingLevel, rule, TrimExcerpt(excerpt)));
            if (findingLevel > level)
                level = findingLevel;
        }
    }

    /// <summary>内置脚本规则（等级, 正则, 标签）。命令过滤覆盖不到的内容风险在此补齐；可在 security.scriptRules 按标签禁用。</summary>
    private static readonly (ScriptRiskLevel Level, string Pattern, string Label)[] BuiltinScriptRules =
    {
        // —— 危险（硬拒绝）——
        (ScriptRiskLevel.Blocked, @":\(\)\s*\{\s*:\s*\|\s*:\s*&\s*\}\s*;", "fork 炸弹"),
        (ScriptRiskLevel.Blocked, @"\b(curl|wget)\b[^|\n]*\|\s*(sudo\s+)?(ba|z|da|k)?sh\b", "下载内容直接管道给 shell 执行"),
        (ScriptRiskLevel.Blocked, @">\s*/dev/(sd|nvme|hd|mmcblk|loop)", "直接写裸盘设备"),
        (ScriptRiskLevel.Blocked, @"\bdd\b[^|\n]*\bof=/dev/", "dd 写设备"),
        (ScriptRiskLevel.Blocked, @"\b(ba|z)?sh\s+-i\s+>&?\s*/dev/tcp/|/dev/tcp/", "反弹 shell"),
        (ScriptRiskLevel.Blocked, @"\bnc\b[^|\n]*\s(-e|-\-exec)\b", "nc 反弹执行"),
        // —— 可疑（进审批并展示）——
        (ScriptRiskLevel.Suspicious, @"id_rsa|id_ed25519|authorized_keys|/\.ssh/", "涉及 SSH 私钥/授权文件"),
        (ScriptRiskLevel.Suspicious, @"/etc/(shadow|passwd|sudoers)", "修改系统账号文件"),
        (ScriptRiskLevel.Suspicious, @"history\s+-c|\.\s*bash_history", "清除操作历史"),
        (ScriptRiskLevel.Suspicious, @"\bcrontab\b|/etc/cron", "修改定时任务"),
        (ScriptRiskLevel.Suspicious, @"base64\s+(-d|--decode)", "base64 解码执行(可能绕过检测)"),
        (ScriptRiskLevel.Suspicious, @"\beval\b", "eval 动态执行"),
        (ScriptRiskLevel.Suspicious, @"chmod\s+(-R\s+)?777", "放开全局可写权限"),
        (ScriptRiskLevel.Suspicious, @"mkswap|swapon|fdisk|parted", "磁盘分区操作"),
        (ScriptRiskLevel.Suspicious, @"\b(useradd|userdel|groupadd|passwd)\b", "账号变更"),
    };

    /// <summary>内置规则标签清单（设置页提示"可禁用的内置规则"用）。</summary>
    public static IReadOnlyList<string> BuiltinRuleLabels =>
        BuiltinScriptRules.Select(r => r.Label).ToArray();

    /// <summary>
    /// 当前生效的脚本专用规则 = 内置规则(按 security.scriptRules 禁用清单过滤) + 自定义危险/可疑正则。
    /// 命令过滤 Blocked/Sensitive 扫描不在此列（始终生效，不受 Enabled 开关影响）。
    /// </summary>
    private IEnumerable<(ScriptRiskLevel Level, string Pattern, string Label)> EffectiveRules()
    {
        var cfg = _options.ScriptRisk;
        HashSet<string>? disabled = cfg.DisabledBuiltinRuleLabels is { Length: > 0 } labels
            ? new HashSet<string>(labels, StringComparer.OrdinalIgnoreCase)
            : null;

        if (cfg.Enabled)
        {
            foreach (var rule in BuiltinScriptRules)
            {
                if (disabled is null || !disabled.Contains(rule.Label))
                    yield return rule;
            }
        }

        if (cfg.ExtraBlockedPatterns is { Length: > 0 } blocked)
        {
            foreach (var pattern in blocked)
            {
                if (!string.IsNullOrWhiteSpace(pattern))
                    yield return (ScriptRiskLevel.Blocked, pattern.Trim(), "自定义:危险规则");
            }
        }

        if (cfg.ExtraSensitivePatterns is { Length: > 0 } sensitive)
        {
            foreach (var pattern in sensitive)
            {
                if (!string.IsNullOrWhiteSpace(pattern))
                    yield return (ScriptRiskLevel.Suspicious, pattern.Trim(), "自定义:可疑规则");
            }
        }
    }

    /// <summary>生成审批弹窗展示的风险报告文本（规则结果为权威；助手摘要单独标注）。</summary>
    public static string FormatReport(ScriptRiskReport report, string? assistantNote = null)
    {
        var sb = new StringBuilder();
        sb.Append("【脚本风险分析(服务端规则扫描)】").Append(report.FileName)
          .Append(" | 结论: ").Append(report.LevelText)
          .Append(" | 行数: ").Append(report.LineCount)
          .Append(" | sha256: ").Append(report.Sha256).Append('.');

        if (!string.IsNullOrWhiteSpace(report.Note))
            sb.AppendLine().Append("说明: ").Append(report.Note);

        foreach (var f in report.Findings)
        {
            var tag = f.Level == ScriptRiskLevel.Blocked ? "危险" : "敏感";
            sb.AppendLine().Append($"- L{f.Line} [{tag}] {f.Rule}: {f.Excerpt}");
        }
        if (report.Findings.Count >= MaxFindings)
            sb.AppendLine("- (命中过多, 已截断)");

        if (!string.IsNullOrWhiteSpace(assistantNote))
            sb.AppendLine().Append("助手评估(仅供参考, 非权威): ").Append(assistantNote.Trim());

        return sb.ToString();
    }

    private static string TrimExcerpt(string line)
    {
        var t = line.Trim();
        return t.Length <= MaxExcerptChars ? t : t[..MaxExcerptChars] + "…";
    }
}
