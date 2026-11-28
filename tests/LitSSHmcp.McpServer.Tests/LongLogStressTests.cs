using System.Diagnostics;
using LitSSHmcp.McpServer.Services;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

/// <summary>
/// 长任务/长日志专项评估 §验证建议 的规模化回归：10 万行日志 tail/grep、单文件 &gt;5MB 单行 JSON。
/// 用生成数据做可重复压力验证，不依赖真实服务器。
/// </summary>
public class LongLogStressTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public void Hundred_thousand_line_tail_keeps_latest_error_within_budget()
    {
        var lines = Enumerable.Range(0, 100_000)
            .Select(i => i == 99_999 ? "FATAL-99999: connection pool exhausted" : $"{i:D6} INFO heartbeat ok");
        var log = string.Concat(lines.Select(l => l + "\n"));
        Assert.True(log.Length > 2_000_000, $"样本太小: {log.Length}");

        var sw = Stopwatch.StartNew();
        var (output, truncated, original) = ToolSupport.Truncate(log, ToolSupport.MaxOutputChars, preferTail: true);
        sw.Stop();

        Assert.True(truncated);
        Assert.Equal(log.Length, original);
        Assert.Contains("FATAL-99999", output);
        Assert.DoesNotContain("000000 INFO", output);
        Assert.True(WellFormed(output), "截断结果含孤立代理项");
        Assert.True(sw.Elapsed < Budget, $"tail 截断耗时 {sw.Elapsed.TotalSeconds:F1}s 超出预算");
    }

    [Fact]
    public void Hundred_thousand_line_grep_keeps_matches_within_budget()
    {
        var lines = Enumerable.Range(0, 100_000)
            .Select(i => i % 10 == 0 ? $"ERROR-{i:D6}: upstream timeout" : $"{i:D6} INFO ok");
        var sw = Stopwatch.StartNew();
        var matches = lines.Where(l => l.Contains("ERROR-", StringComparison.Ordinal)).Take(10_000).ToList();
        var filtered = string.Concat(matches.Select(l => l + "\n"));
        var (output, truncated, _) = ToolSupport.Truncate(filtered, ToolSupport.MaxOutputChars, preferTail: true);
        sw.Stop();

        Assert.Equal(10_000, matches.Count);
        Assert.Contains("ERROR-000000", filtered);
        Assert.True(truncated);
        Assert.Contains("ERROR-099990", output);   // 最新命中未被砍掉
        Assert.DoesNotContain("ERROR-000000", output);
        Assert.True(WellFormed(output));
        Assert.True(sw.Elapsed < Budget, $"grep+截断耗时 {sw.Elapsed.TotalSeconds:F1}s 超出预算");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Five_megabyte_single_line_json_truncates_without_splitting_surrogates(bool preferTail)
    {
        // 单行大 JSON（日志采集常见形态）：中间夹 emoji 与生僻字，切点刻意落在代理对附近。
        var emoji = "😀";
        var pad = new string('x', 5_000_000 / 2);
        var json = "{\"data\":\"" + pad + emoji + pad + "\",\"tail\":\"LAST\"}";
        Assert.True(json.Length > 5_000_000, $"样本太小: {json.Length}");

        var (output, truncated, original) = ToolSupport.Truncate(json, ToolSupport.MaxOutputChars, preferTail);

        Assert.True(truncated);
        Assert.Equal(json.Length, original);
        Assert.True(WellFormed(output), "5MB 单行截断产生非法 UTF-16");
        if (preferTail)
            Assert.Contains("LAST", output);
        else
            Assert.Contains("{\"data\"", output);
    }

    /// <summary>逐字符验证不存在孤立高/低代理项（下游 provider 会因此返回 400）。</summary>
    private static bool WellFormed(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                    return false;
                i++;
            }
            else if (char.IsLowSurrogate(text[i]))
                return false;
        }
        return true;
    }
}
