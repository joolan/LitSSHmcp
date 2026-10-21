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
    Blocked,
    /// <summary>包含 shell 拼接/重定向/子表达式，或嵌套 shell（硬拒绝）。</summary>
    UnsafeSyntax,
    /// <summary>疑似交互式/长驻命令，会一直占住终端（硬拒绝）。</summary>
    Interactive
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

    /// <summary>
    /// 拒绝 shell 拼接、管道、重定向、子表达式、脚本块、成员访问等可绕开“单条命令”边界的语法。
    /// 引号内的普通字符（如提交信息里的 <c>|</c>）不受影响；双引号内的子表达式仍拒绝。
    /// </summary>
    public static bool ContainsUnsafeShellSyntax(string? command)
    {
        if (string.IsNullOrEmpty(command))
            return false;

        var inSingle = false;
        var inDouble = false;
        var sawToken = false;
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c is '\r' or '\n')
                return true;

            if (inSingle)
            {
                // PowerShell 单引号内用两个单引号表示一个字面单引号。
                if (c == '\'')
                {
                    if (i + 1 < command.Length && command[i + 1] == '\'')
                        i++;
                    else
                        inSingle = false;
                }
                continue;
            }

            if (inDouble)
            {
                if (c == '`')
                    return true;
                if (c == '$' && i + 1 < command.Length && command[i + 1] is '(' or '{')
                    return true;
                if (c == '"')
                {
                    if (i + 1 < command.Length && command[i + 1] == '"')
                        i++;
                    else
                        inDouble = false;
                }
                continue;
            }

            if (c is ' ' or '\t')
                continue;
            if (char.IsWhiteSpace(c))
                return true;

            if (c == '\'')
            {
                inSingle = true;
                sawToken = true;
                continue;
            }
            if (c == '"')
            {
                inDouble = true;
                sawToken = true;
                continue;
            }

            // 仅允许行首的 PowerShell 调用运算符：& 'C:\Program Files\git.exe' status
            if (c == '&' && !sawToken && (i + 1 >= command.Length || command[i + 1] != '&'))
            {
                sawToken = true;
                continue;
            }

            if (c is ';' or '|' or '&' or '>' or '<' or '`' or '{' or '}' or '(' or ')' or '[' or ']')
                return true;
            if (c == '$' && i + 1 < command.Length && command[i + 1] is '(' or '{')
                return true;
            if (c == '@' && i + 1 < command.Length && command[i + 1] == '(')
                return true;

            sawToken = true;
        }

        return inSingle || inDouble;
    }

    public static bool IsAutoApproved(string? command, IReadOnlyCollection<string>? rules)
        => !string.IsNullOrWhiteSpace(command) &&
           (rules ?? Array.Empty<string>()).Any(rule =>
               string.Equals(rule?.Trim(), command.Trim(), StringComparison.Ordinal));

    private static bool IsNestedShell(string? exe)
        => exe is not null && exe.ToLowerInvariant() is
            "cmd" or "powershell" or "pwsh" or "bash" or "sh" or "zsh" or "wsl" or "cscript" or "wscript";

    /// <summary>判定命令是否可执行：白名单 + 危险规则 + 单条命令语法。</summary>
    public static LocalPolicyResult Check(string? command, IReadOnlyCollection<string>? allowedCommands)
    {
        if (string.IsNullOrWhiteSpace(command))
            return LocalPolicyResult.Blocked;

        if (ContainsUnsafeShellSyntax(command))
            return LocalPolicyResult.UnsafeSyntax;

        foreach (var pattern in DangerousPatterns)
        {
            if (pattern.IsMatch(command))
                return LocalPolicyResult.Blocked;
        }

        if (IsLikelyInteractive(command))
            return LocalPolicyResult.Interactive;

        var exe = FirstExecutable(command);
        if (exe is null)
            return LocalPolicyResult.Blocked;
        if (IsNestedShell(exe))
            return LocalPolicyResult.UnsafeSyntax;

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

    /// <summary>长驻会话策略：允许 tail -f / dev server / 下载等，但仍拦截裸 shell、REPL 与后台逃逸。</summary>
    public static LocalPolicyResult CheckForLongRunning(string? command, IReadOnlyCollection<string>? allowedCommands)
    {
        if (string.IsNullOrWhiteSpace(command))
            return LocalPolicyResult.Blocked;

        if (ContainsUnsafeShellSyntax(command))
            return LocalPolicyResult.UnsafeSyntax;

        foreach (var pattern in DangerousPatterns)
        {
            if (pattern.IsMatch(command))
                return LocalPolicyResult.Blocked;
        }

        if (Regex.IsMatch(command, @"\bStart-(Process|Job)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled))
            return LocalPolicyResult.Blocked;

        if (IsUnsupportedForBackground(command))
            return LocalPolicyResult.Interactive;

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

    public static bool IsUnsupportedForBackground(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var exe = FirstExecutable(command)?.ToLowerInvariant();
        if (exe is null)
            return false;

        if (exe is "vim" or "vi" or "nvim" or "nano" or "emacs" or "pico" or "less" or "more"
            or "man" or "top" or "htop" or "btop" or "watch" or "screen" or "tmux" or "mc"
            or "powershell" or "pwsh" or "cmd" or "bash" or "sh" or "zsh" or "wsl"
            or "start-process" or "start-job")
            return true;

        var tokens = SplitTokens(command);
        var args = tokens.Skip(1).ToList();

        if (exe is "python" or "python3" or "node" or "irb" or "ruby" && args.Count == 0)
            return true;

        if (exe is "docker" or "kubectl" && args.Any(a => a is "-it" or "-ti"))
            return true;

        if (exe == "git" && args.Count >= 2 &&
            (args[0] == "rebase" && args.Contains("-i") || args[0] == "add" && args.Contains("-p")))
            return true;

        if (exe is "mysql" or "psql" or "redis-cli" or "sqlite3" or "mongo" or "mongosh" && args.Count == 0)
            return true;

        return false;
    }

    /// <summary>
    /// 粗判疑似交互式/长驻命令（编辑器、分页器、全屏监视、tail -f、无参 REPL、
    /// docker/kubectl exec -it、无参数据库客户端等）——这类会一直占住共享终端。
    /// 采用保守规则，避免误伤常见的非交互用法（如 python script.py、cmd /c、docker ps）。
    /// </summary>
    public static bool IsLikelyInteractive(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var exe = FirstExecutable(command)?.ToLowerInvariant();
        if (exe is null)
            return false;

        if (exe is "vim" or "vi" or "nvim" or "nano" or "emacs" or "pico" or "less" or "more"
            or "man" or "top" or "htop" or "btop" or "watch" or "screen" or "tmux" or "mc")
            return true;

        var tokens = SplitTokens(command);
        var args = tokens.Skip(1).ToList();

        if (exe == "tail" && args.Any(a => a is "-f" or "--follow" || a.StartsWith("--follow=", StringComparison.Ordinal)))
            return true;

        if (exe == "ping" && args.Any(a => a is "-t" or "/t"))
            return true;

        // 无参 REPL / 嵌套交互式 shell
        if (exe is "python" or "python3" or "node" or "pwsh" or "powershell" or "cmd" or "irb" or "ruby" && args.Count == 0)
            return true;

        // 交互式容器/集群会话
        if (exe is "docker" or "kubectl" && args.Any(a => a is "-it" or "-ti"))
            return true;

        if (exe == "git" && args.Count >= 2 &&
            (args[0] == "rebase" && args.Contains("-i") || args[0] == "add" && args.Contains("-p")))
            return true;

        // 无参数据库客户端
        if (exe is "mysql" or "psql" or "redis-cli" or "sqlite3" or "mongo" or "mongosh" && args.Count == 0)
            return true;

        return false;
    }

    private static List<string> SplitTokens(string command)
    {
        var s = command.TrimStart();
        if (s.StartsWith('&'))
            s = s[1..].TrimStart();
        else if (s.StartsWith("call ", StringComparison.OrdinalIgnoreCase))
            s = s[5..].TrimStart();
        return s.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
