using LitSSHmcp.Core.Services.Security;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 把「日志文件 + 常用运维动作」拼成可直接粘贴到交互式终端的 POSIX shell 命令。
/// 所有路径 / 关键字都经 <see cref="ShellQuote.Single"/> 转义；时间过滤按行首 ISO 时间戳
/// （yyyy-MM-dd HH:mm:ss，也接受 T 分隔）做词法比较，与远端本地时间一致。
/// </summary>
public static class LogTerminalCommandBuilder
{
    public const int DefaultMaxLines = 200;
    public const int DefaultErrorScanLines = 2000;
    public const int MaxMaxLines = 5000;
    public const int MaxScanLines = 200_000;

    /// <summary>
    /// 时间过滤：支持行首 ISO 时间戳（yyyy-MM-dd HH:mm:ss / T）、nginx 错误日志的
    /// yyyy/MM/dd HH:mm:ss，以及 nginx/Apache 访问日志的 [dd/Mon/yyyy:HH:mm:ss。
    /// 未识别到时间戳时向 stderr 打一条提示；命中时间戳后的续行也会保留。
    /// 必须保持单行——命令会原样写入交互式终端的输入流，多行会被 shell 逐行提前执行。
    /// </summary>
    private const string TimeFilterAwk = """BEGIN { split("Jan Feb Mar Apr May Jun Jul Aug Sep Oct Nov Dec", X, " "); for (i = 1; i <= 12; i++) M[X[i]] = i } { ts = substr($0, 1, 19); if (ts ~ /^[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]/) { if (substr(ts, 11, 1) == "T") ts = substr(ts, 1, 10) " " substr(ts, 12); seen = 1; inRange = (ts >= s && ts <= e); if (inRange) print } else if ($0 ~ /^[0-9][0-9][0-9][0-9]\/[0-9][0-9]\/[0-9][0-9] [0-9][0-9]:[0-9][0-9]:[0-9][0-9]/) { ts = substr($0, 1, 10); gsub(/\//, "-", ts); ts = ts " " substr($0, 12, 8); seen = 1; inRange = (ts >= s && ts <= e); if (inRange) print } else if (match($0, /\[[0-9][0-9]\/[A-Za-z][A-Za-z][A-Za-z]\/[0-9][0-9][0-9][0-9]:[0-9][0-9]:[0-9][0-9]:[0-9][0-9]/)) { t = substr($0, RSTART, 21); d = substr(t, 2, 2); mon = substr(t, 5, 3); y = substr(t, 9, 4); hh = substr(t, 14, 2); mm = substr(t, 17, 2); ss = substr(t, 20, 2); m = M[mon]; if (m > 0) { ts = y "-" (m < 10 ? "0" : "") m "-" d " " hh ":" mm ":" ss; seen = 1; inRange = (ts >= s && ts <= e); if (inRange) print } else if (inRange) print } else if (inRange) print } END { if (!seen) print "litssh: 未识别到 ISO / nginx / Apache 时间戳，时间过滤可能未命中" > "/dev/stderr" }""";

    private const string ErrorPattern =
        "error|exception|fatal|panic|fail(ed|ure)?|timeout|timed out|refused|denied|traceback|outofmemory|oom|segfault|core dumped";

    public static int ClampLines(int lines)
        => lines <= 0 ? DefaultMaxLines : Math.Clamp(lines, 1, MaxMaxLines);

    /// <summary>查看最新 N 条。</summary>
    public static string TailLatest(string path, int lines = DefaultMaxLines)
        => $"tail -n {ClampLines(lines)} -- {Quote(path)}";

    /// <summary>从此刻开始实时跟踪（Ctrl+C 结束）。</summary>
    public static string Follow(string path)
        => $"tail -n 0 -F -- {Quote(path)}";

    /// <summary>从此刻开始实时跟踪并只显示匹配关键字/正则的行。</summary>
    public static string FollowFiltered(string path, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return Follow(path);
        return $"{Follow(path)} | grep --line-buffered -E -i -- {Quote(pattern)}";
    }

    /// <summary>在最近 1 小时（远端时间）的日志中搜索关键字/正则；关键字为空则只按时间过滤。</summary>
    public static string SearchLastHour(string path, string? pattern, int maxLines = DefaultMaxLines)
    {
        const string since = "\"$(date -d '1 hour ago' '+%Y-%m-%d %H:%M:%S')\"";
        const string now = "\"$(date '+%Y-%m-%d %H:%M:%S')\"";
        return SearchByTime(path, since, now, pattern, maxLines);
    }

    /// <summary>在指定时间范围内搜索；时间按远端日志行首时间戳解释（含起止）。</summary>
    public static string SearchBetween(string path, string start, string end, string? pattern, int maxLines = DefaultMaxLines)
        => SearchByTime(path, Quote(start), Quote(end), pattern, maxLines);

    public static string SearchByTime(string path, string sinceArg, string untilArg, string? pattern, int maxLines = DefaultMaxLines)
    {
        var command = $"tail -n {MaxScanLines} -- {Quote(path)} | awk -v s={sinceArg} -v e={untilArg} {Quote(TimeFilterAwk)}";
        if (!string.IsNullOrWhiteSpace(pattern))
            command += $" | grep -E -i -- {Quote(pattern)}";
        return command + $" | tail -n {ClampLines(maxLines)}";
    }

    /// <summary>最近 N 行中的错误 / 异常关键字。</summary>
    public static string Errors(string path, int scanLines = DefaultErrorScanLines, int maxLines = DefaultMaxLines)
        => $"tail -n {ClampLines(scanLines)} -- {Quote(path)} | grep -n -E -i -- {Quote(ErrorPattern)} | tail -n {ClampLines(maxLines)}";

    /// <summary>关键字上下文（前后各 contextLines 行）。</summary>
    public static string GrepContext(string path, string pattern, int contextLines = 3, int maxLines = DefaultMaxLines)
    {
        var context = Math.Clamp(contextLines, 0, 50);
        return $"grep -n -E -i -C {context} -- {Quote(pattern)} {Quote(path)} | tail -n {ClampLines(maxLines)}";
    }

    /// <summary>统计关键字/正则出现次数。</summary>
    public static string Count(string path, string pattern)
        => $"grep -c -E -i -- {Quote(pattern)} {Quote(path)}";

    /// <summary>用 less 打开并跳到末尾（进入后可 / 搜索、q 退出）。</summary>
    public static string LessAtEnd(string path)
        => $"less +G -- {Quote(path)}";

    /// <summary>
    /// 筛选日志并写入远端文件（用于大日志切片后下载）。
    /// <paramref name="maxScanLines"/> &lt;= 0 表示扫描整个文件；<paramref name="since"/>/<paramref name="until"/>
    /// 同时提供时按时间过滤；<paramref name="pattern"/> 为空表示不按关键字过滤。
    /// </summary>
    public static string SliceToFile(
        string path,
        string outputPath,
        string? pattern = null,
        int maxScanLines = 0,
        string? since = null,
        string? until = null)
    {
        if (string.Equals(path.TrimEnd('/'), outputPath.TrimEnd('/'), StringComparison.Ordinal))
            throw new ArgumentException("输出文件不能与源日志相同。", nameof(outputPath));

        var hasPattern = !string.IsNullOrWhiteSpace(pattern);
        var hasTime = !string.IsNullOrWhiteSpace(since) && !string.IsNullOrWhiteSpace(until);
        var source = maxScanLines > 0
            ? $"tail -n {Math.Clamp(maxScanLines, 1, 100_000_000)} -- {Quote(path)}"
            : $"cat -- {Quote(path)}";

        string command;
        if (hasTime)
        {
            command = $"{source} | awk -v s={Quote(since!)} -v e={Quote(until!)} {Quote(TimeFilterAwk)}";
            if (hasPattern)
                command += $" | grep -E -i -- {Quote(pattern!)}";
        }
        else if (hasPattern)
        {
            // 整个文件 + 关键字时直接 grep，省掉 cat，20G 级日志更省 IO。
            command = maxScanLines > 0
                ? $"{source} | grep -E -i -- {Quote(pattern!)}"
                : $"grep -E -i -- {Quote(pattern!)} {Quote(path)}";
        }
        else
        {
            command = source;
        }

        var output = Quote(outputPath);
        return $"{command} > {output}; wc -l {output}; ls -lh {output}";
    }

    private static string Quote(string value)
        => ShellQuote.Single(value.Replace('\r', ' ').Replace('\n', ' '));
}
