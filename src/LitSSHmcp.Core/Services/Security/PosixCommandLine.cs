using System.Text;

namespace LitSSHmcp.Core.Services.Security;

/// <summary>
/// 把一条简单的 POSIX 命令行拆成 argv。只处理引号与反斜杠转义，<b>不展开</b>变量、通配符、命令替换或管道；
/// 结果应逐参数经 <see cref="ShellQuote.Single"/> 转义后再交给远端 shell，从而保留 argv 语义并杜绝注入。
/// </summary>
public static class PosixCommandLine
{
    /// <summary>拆分命令行；引号未闭合、包含换行/NUL 或缺少命令时返回 false。</summary>
    public static bool TrySplit(string? commandLine, out IReadOnlyList<string> args, out string? error)
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
