using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class ToolResultFormatterTests
{
    [Fact]
    public void Decodes_unicode_escapes_in_plain_json()
    {
        var raw = """{"name":"\u865A\u62DF\u673A"}""";

        var formatted = ToolResultFormatter.Format(raw, pretty: true);

        Assert.Contains("虚拟机", formatted);
        Assert.DoesNotContain("\\u865A", formatted);
    }

    [Fact]
    public void Unwraps_nested_json_string_in_mcp_content()
    {
        // 模拟 MCP SDK 默认编码器的输出：内层 DTO 的引号被写成 \u0022，中文被写成 \uXXXX。
        var raw = """{"content":[{"type":"text","text":"{\u0022success\u0022:true,\u0022name\u0022:\u0022\u865A\u62DF\u673A\u0022}"}],"resultType":"complete"}""";

        var formatted = ToolResultFormatter.Format(raw, pretty: true);

        Assert.Contains("虚拟机", formatted);
        Assert.DoesNotContain("\\u0022", formatted);
        Assert.DoesNotContain("\\u865A", formatted);
    }

    [Fact]
    public void Normalize_is_compact_and_unescaped_for_model()
    {
        var raw = """{"content":[{"type":"text","text":"{\u0022name\u0022:\u0022\u865A\u62DF\u673A\u0022}"}]}""";

        var normalized = ToolResultFormatter.Normalize(raw);

        Assert.Contains("虚拟机", normalized);
        Assert.DoesNotContain("\\u865A", normalized);
        Assert.DoesNotContain("\\u0022", normalized);
        Assert.DoesNotContain("\n", normalized);   // 紧凑：无缩进换行，省 token
        Assert.DoesNotContain(": ", normalized);   // 紧凑：无键值间空格
    }

    [Fact]
    public void Non_json_passes_through_unchanged()
    {
        const string raw = "hello 世界\nline2";

        Assert.Equal(raw, ToolResultFormatter.Format(raw, pretty: true));
        Assert.Equal(raw, ToolResultFormatter.Normalize(raw));
    }

    [Fact]
    public void Empty_and_whitespace_returned_as_is()
    {
        Assert.Equal(string.Empty, ToolResultFormatter.Format(null, pretty: true));
        Assert.Equal("   ", ToolResultFormatter.Format("   ", pretty: true));
    }

    [Fact]
    public void Malformed_json_passes_through_unchanged()
    {
        const string raw = """{"name":"\u865A""";

        Assert.Equal(raw, ToolResultFormatter.Format(raw, pretty: true));
    }
}

