using System.Text;

namespace LitSSHmcp.Core.Services.Security;

/// <summary>命令语法错误类型；用于把“为什么拒绝”与具体策略（白名单/审批/脚本扫描）解耦。</summary>
public enum CommandSyntaxError
{
    None,
    Empty,
    Newline,
    UnterminatedQuote,
    CommandSeparator,
    Pipeline,
    Redirection,
    SubExpression,
    ScriptBlock,
    UnsupportedWhitespace
}

/// <summary>
/// 命令安全解析器（唯一实现）：
/// <list type="bullet">
/// <item><see cref="ValidateWindowsSingleCommand"/>：本机 PowerShell 单命令语法边界；</item>
/// <item><see cref="FirstExecutableWindows"/> / <see cref="SplitWindowsTokens"/>：取出可执行文件与参数；</item>
/// <item><see cref="TrySplitPosix"/>：远端 POSIX argv 拆分（不展开变量/通配符/管道）。</item>
/// </list>
/// 具体策略（白名单、危险规则、审批、脚本扫描）仍各自负责，但不得再自行实现一套解析规则。
/// </summary>
public static class CommandLineSyntax
{
    /// <summary>本机单命令是否包含可绕开边界的 shell 语法（引号内字面量不受影响）。</summary>
    public static bool ContainsUnsafeWindowsShellSyntax(string? command) =>
        ValidateWindowsSingleCommand(command) is not (CommandSyntaxError.None or CommandSyntaxError.Empty);

    /// <summary>校验一条本机单命令；空字符串返回 <see cref="CommandSyntaxError.Empty"/>，纯空白返回 None。</summary>
    public static CommandSyntaxError ValidateWindowsSingleCommand(string? command)
    {
        if (string.IsNullOrEmpty(command))
            return CommandSyntaxError.Empty;

        var inSingle = false;
        var inDouble = false;
        var sawToken = false;
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c is '\r' or '\n')
                return CommandSyntaxError.Newline;

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
                    return CommandSyntaxError.SubExpression;
                if (c == '$' && i + 1 < command.Length && command[i + 1] is '(' or '{')
                    return CommandSyntaxError.SubExpression;
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
                return CommandSyntaxError.UnsupportedWhitespace;

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

            switch (c)
            {
                case ';':
                    return CommandSyntaxError.CommandSeparator;
                case '|':
                    return CommandSyntaxError.Pipeline;
                case '&':
                    return CommandSyntaxError.CommandSeparator;
                case '>' or '<':
                    return CommandSyntaxError.Redirection;
                case '`':
                    return CommandSyntaxError.SubExpression;
                case '{' or '}':
                    return CommandSyntaxError.ScriptBlock;
                case '(' or ')' or '[' or ']':
                    return CommandSyntaxError.SubExpression;
            }

            if (c == '$' && i + 1 < command.Length && command[i + 1] is '(' or '{')
                return CommandSyntaxError.SubExpression;
            if (c == '@' && i + 1 < command.Length && command[i + 1] == '(')
                return CommandSyntaxError.SubExpression;

            sawToken = true;
        }

        return inSingle || inDouble ? CommandSyntaxError.UnterminatedQuote : CommandSyntaxError.None;
    }

    /// <summary>取出命令行首的可执行文件名（忽略前导 &amp; / call、引号、路径与 .exe 扩展名）。</summary>
    public static string? FirstExecutableWindows(string? command)
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

        try { token = Path.GetFileNameWithoutExtension(token); }
        catch { /* 非法路径字符：按原样 */ }

        return token.Length == 0 ? null : token;
    }

    /// <summary>按空白拆分本机命令行，并去掉前导 &amp; / call。</summary>
    public static IReadOnlyList<string> SplitWindowsTokens(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return Array.Empty<string>();

        var s = command.TrimStart();
        if (s.StartsWith('&'))
            s = s[1..].TrimStart();
        else if (s.StartsWith("call ", StringComparison.OrdinalIgnoreCase))
            s = s[5..].TrimStart();
        return s.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>是否是嵌套 shell（会把单命令边界变成任意脚本执行）。</summary>
    public static bool IsNestedShell(string? executable) =>
        executable is not null && executable.ToLowerInvariant() is
            "cmd" or "powershell" or "pwsh" or "bash" or "sh" or "zsh" or "wsl" or "cscript" or "wscript";

    /// <summary>
    /// 把一条简单的 POSIX 命令行拆成 argv。只处理引号与反斜杠转义，<b>不展开</b>变量、通配符、命令替换或管道；
    /// 结果应逐参数经 <see cref="ShellQuote.Single"/> 转义后再交给远端 shell，从而保留 argv 语义并杜绝注入。
    /// </summary>
    public static bool TrySplitPosix(string? commandLine, out IReadOnlyList<string> args, out string? error)
    {
        args = Array.Empty<string>();
        error = null;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            error = "命令不能为空。";
            return false;
        }

        var result = new List<string>();
        var buffer = new StringBuilder();
        var inArg = false;
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c is '\r' or '\n' or '\0')
            {
                error = "命令不能包含换行或 NUL 字符。";
                return false;
            }

            if (inSingle)
            {
                if (c == '\'')
                    inSingle = false;
                else
                    buffer.Append(c);
                continue;
            }

            if (inDouble)
            {
                if (c == '"')
                {
                    inDouble = false;
                }
                else if (c == '\\')
                {
                    if (++i >= commandLine.Length)
                    {
                        error = "命令以未完成的反斜杠结尾。";
                        return false;
                    }
                    var escaped = commandLine[i];
                    if (escaped is '\r' or '\n' or '\0')
                    {
                        error = "命令不能包含换行或 NUL 字符。";
                        return false;
                    }
                    if (escaped is '"' or '\\' or '$' or '`')
                        buffer.Append(escaped);
                    else
                        buffer.Append('\\').Append(escaped);
                }
                else
                {
                    buffer.Append(c);
                }
                continue;
            }

            if (c == '\'')
            {
                inSingle = true;
                inArg = true;
            }
            else if (c == '"')
            {
                inDouble = true;
                inArg = true;
            }
            else if (c == '\\')
            {
                if (++i >= commandLine.Length)
                {
                    error = "命令以未完成的反斜杠结尾。";
                    return false;
                }
                var escaped = commandLine[i];
                if (escaped is '\r' or '\n' or '\0')
                {
                    error = "命令不能包含换行或 NUL 字符。";
                    return false;
                }
                buffer.Append(escaped);
                inArg = true;
            }
            else if (c is ' ' or '\t')
            {
                if (inArg)
                {
                    result.Add(buffer.ToString());
                    buffer.Clear();
                    inArg = false;
                }
            }
            else if (char.IsWhiteSpace(c))
            {
                error = "命令包含不支持的空白字符。";
                return false;
            }
            else
            {
                buffer.Append(c);
                inArg = true;
            }
        }

        if (inSingle || inDouble)
        {
            error = "命令包含未闭合的引号。";
            return false;
        }
        if (inArg)
            result.Add(buffer.ToString());
        if (result.Count == 0)
        {
            error = "命令不能为空。";
            return false;
        }

        args = result;
        return true;
    }
}
