using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class MemoryQueryRewriterTests
{
    [Fact]
    public void Expands_chinese_and_english_ops_synonyms()
    {
        var terms = MemoryQueryRewriter.ExpandTerms("磁盘满后重启 nginx");

        Assert.Contains("磁盘", terms);
        Assert.Contains("disk", terms);
        Assert.Contains("重启", terms);
        Assert.Contains("restart", terms);
        Assert.Contains("nginx", terms);
    }

    [Fact]
    public void Deduplicates_and_caps_terms()
    {
        var terms = MemoryQueryRewriter.ExpandTerms("log log LOG 日志 日志");

        Assert.Contains("log", terms);
        Assert.Contains("日志", terms);
        Assert.True(terms.Count <= 24);
        Assert.Equal(terms.Count, terms.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
