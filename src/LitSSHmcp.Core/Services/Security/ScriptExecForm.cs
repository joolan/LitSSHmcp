using System.Text.RegularExpressions;

namespace LitSSHmcp.Core.Services.Security;

/// <summary>
/// 识别"执行脚本文件"形态的命令（如 <c>bash x.sh</c>、<c>python3 /opt/a.py</c>、<c>powershell -File a.ps1</c>、<c>./deploy.sh</c>）。
/// 用于在执行前强制"读取内容 → 规则扫描 → 用户同意"的脚本安全门；
/// <c>bash -c "…"</c> 等内联形态**不**视为脚本文件（其内容就是命令行本身, 已由命令过滤器直接扫描）。
/// </summary>
public static class ScriptExecForm
{
    private static readonly string[] Interpreters =
    {
        "bash", "sh", "zsh", "dash", "ksh",
        "python", "pwsh", "powershell", "perl", "ruby"
    };

    /// <summary>版本化解释器(python3.12 / python2.7 / py 等)。</summary>
    private static readonly Regex VersionedInterpreter =
        new(@"^(python[0-9.]*|py)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>命令首段允许跳过的前缀词。</summary>
    private static readonly string[] SkipPrefixes = { "sudo", "env", "nohup", "setsid", "time", "exec" };

    /// <summary>
    /// 尝试把命令解析为脚本执行。true 时 <paramref name="scriptPath"/> 为脚本文件参数，
    /// <paramref name="interpreter"/> 为解释器（直接执行如 ./x.sh 时为空字符串）。
    /// </summary>
    public static bool TryParse(string? command, out string interpreter, out string scriptPath)
    {
        interpreter = string.Empty;
        scriptPath = string.Empty;
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var parts = command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        var i = 0;
        while (i < parts.Length && SkipPrefixes.Contains(parts[i], StringComparer.OrdinalIgnoreCase))
            i++;
        if (i >= parts.Length)
            return false;

        var head = StripQuotes(parts[i]);

        // 直接执行脚本文件: ./x.sh / /opt/x.sh / x.sh
        if (ScriptRiskAnalyzer.IsScriptFile(head) &&
            (head.StartsWith('.') || head.StartsWith('/') || head.StartsWith('~') || head.Contains('/')))
        {
            scriptPath = head;
            return true;
        }

        if (!Interpreters.Contains(head, StringComparer.OrdinalIgnoreCase) && !VersionedInterpreter.IsMatch(head))
            return false;
        if (parts.Length <= i + 1)
            return false;

        interpreter = head;
        var rest = parts[(i + 1)..];
        for (var j = 0; j < rest.Length; j++)
        {
            var token = StripQuotes(rest[j]);

            // 内联脚本（-c/-lc/-ic）：内容即命令行, 由命令过滤器直接扫描, 不走脚本门
            if (token.Equals("-c", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("-lc", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("-ic", StringComparison.OrdinalIgnoreCase))
                return false;

            // PowerShell -File/-f <path>
            if (token.Equals("-File", StringComparison.OrdinalIgnoreCase) || token.Equals("-f", StringComparison.OrdinalIgnoreCase))
            {
                if (j + 1 >= rest.Length)
                    return false;
                scriptPath = StripQuotes(rest[j + 1]);
                return LooksLikePath(scriptPath);
            }

            if (token.StartsWith('-'))
                continue;   // 解释器选项(如 -u -e -B), 继续找文件参数

            scriptPath = token;
            return LooksLikePath(scriptPath);
        }
        return false;
    }

    private static bool LooksLikePath(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains('/') || value.Contains('\\') || value.Contains('.') || value.StartsWith('~'));

    private static string StripQuotes(string value) =>
        value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]
            ? value[1..^1]
            : value;
}
