using System.Text;

namespace LitSSHmcp.Core.Services.Datasource;

/// <summary>
/// 把 AI 传来的命令行（如 <c>HMSET user:1 name "张 三" age 18</c>）拆成 RESP 参数数组。
/// 支持单引号/双引号包裹含空格的值，双引号内支持 \" 转义，反斜杠可转义任意字符。
/// </summary>
public static class RedisCommandParser
{
    public static string[] Split(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            throw new FormatException("Redis 命令不能为空");

        var source = commandLine;
        var args = new List<string>();
        var token = new StringBuilder();
        var hasToken = false;
        var index = 0;

        while (index < source.Length)
        {
            var current = source[index];

            if (current is '\'' or '"')
            {
                var quote = current;
                index++;
                hasToken = true;

                while (true)
                {
                    if (index >= source.Length)
                        throw new FormatException($"引号未闭合: {quote}");

                    var inner = source[index];
                    if (inner == quote)
                    {
                        index++;
                        break;
                    }

                    if (inner == '\\' && quote == '"' && index + 1 < source.Length)
                    {
                        token.Append(source[index + 1]);
                        index += 2;
                        continue;
                    }

                    token.Append(inner);
                    index++;
                }

                continue;
            }

            if (char.IsWhiteSpace(current))
            {
                if (hasToken)
                {
                    args.Add(token.ToString());
                    token.Clear();
                    hasToken = false;
                }
                index++;
                continue;
            }

            if (current == '\\' && index + 1 < source.Length)
            {
                token.Append(source[index + 1]);
                hasToken = true;
                index += 2;
                continue;
            }

            token.Append(current);
            hasToken = true;
            index++;
        }

        if (hasToken)
            args.Add(token.ToString());

        if (args.Count == 0)
            throw new FormatException("Redis 命令不能为空");

        return args.ToArray();
    }
}
