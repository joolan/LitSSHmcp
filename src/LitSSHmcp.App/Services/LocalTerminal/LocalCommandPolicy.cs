using System.Text.RegularExpressions;

namespace LitSSHmcp.App.Services.LocalTerminal;

/// <summary>本机命令策略判定结果。</summary>
public enum LocalPolicyResult
{
    /// <summary>在白名单内且未命中危险规则。</summary>
    Allowed,
    /// <summary>不在白名单内（硬拒绝）。</summary>
    NotInAllowlist,
    /// <summary>命中危险/无效规则（硬拒绝）。</summary>
    Blocked
}

/// <summary>
/// 本机 CLI 白名单与危险命令判定（硬策略）。与 UI/进程无关，便于单测。
/// </summary>
public static class LocalCommandPolicy
{
    private static readonly Regex[] DangerousPatterns =
    {
        new(@"\brm\s+-[a-z]*r[a-z]*f|\brm\s+-[a-z]*f[a-z]*r", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\brm\s+-[a-z]*rf\s+/(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bmkfs(\.\w+)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bdiskpart\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bformat\s+[a-z]:", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(shutdown|restart-computer|stop-computer)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\breg\s+delete\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bRemove-Item\b[^\r\n]*-Recurse[^\r\n]*-Force", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@":\(\)\s*\{.*\|\s*:\s*&\s*\}\s*;", RegexOptions.Compiled),   // fork bomb
        new(@"\bdd\s+if=", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    };

    /// <summary>取出命令行首的可执行文件名（忽略前导 & / call、引号、路径与 .exe 扩展名）。</summary>
    public static string? FirstExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var s = command.TrimStart();
        if (s.StartsWith('&'))
            s = s[1..].TrimStart();
        else if (s.StartsWith("call ", StringComparison.OrdinalIgnoreCase))
            s = s[5..].TrimStart();

        if (s.Length == 0)
            return null;

        string token;
        if (s[0] is '"' or '\'')
        {
            var quote = s[0];
            var end = s.IndexOf(quote, 1);
            if (end <= 1)
                return null;
            token = s[1..end];
        }
        else
        {
            var idx = s.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
            token = idx < 0 ? s : s[..idx];
        }

        token = token.Trim();
        if (token.Length == 0)
            return null;

        try { token = System.IO.Path.GetFileNameWithoutExtension(token); }
        catch { /* 非法路径字符：按原样 */ }

        return token.Length == 0 ? null : token;
    }

    /// <summary>判定命令是否可执行：白名单 + 危险规则。</summary>
    public static LocalPolicyResult Check(string? command, IReadOnlyCollection<string>? allowedCommands)
    {
        if (string.IsNullOrWhiteSpace(command))
            return LocalPolicyResult.Blocked;

        foreach (var pattern in DangerousPatterns)
        {
            if (pattern.IsMatch(command))
                return LocalPolicyResult.Blocked;
        }

        var exe = FirstExecutable(command);
        if (exe is null)
            return LocalPolicyResult.Blocked;

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in allowedCommands ?? Array.Empty<string>())
        {
            var name = item?.Trim().TrimEnd('*').Trim();
            if (string.IsNullOrEmpty(name))
                continue;
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            try { name = System.IO.Path.GetFileName(name); }
            catch { /* 原样 */ }
            allowed.Add(name);
        }

        return allowed.Contains(exe) ? LocalPolicyResult.Allowed : LocalPolicyResult.NotInAllowlist;
    }
}
