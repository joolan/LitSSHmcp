using System.IO;
using LitSSHmcp.App.Views;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>App「设置 → MCP 工具说明」使用嵌入的 docs/TOOLS.md；守门新增工具确实进入 App 展示。</summary>
public class McpToolsViewTests
{
    [Fact]
    public void Embedded_tools_doc_contains_asset_graph_query_tools()
    {
        var assembly = typeof(McpToolsView).Assembly;
        using var stream = assembly.GetManifestResourceStream("LitSSHmcp.App.docs.TOOLS.md");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var text = reader.ReadToEnd();

        Assert.Contains("### `topology_query`", text);
    }
}
