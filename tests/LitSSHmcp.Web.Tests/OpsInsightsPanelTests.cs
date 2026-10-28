using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>运维视图前端（事件/时间线/资产画像）：node 直跑 Frontend/ops-insights.test.mjs。</summary>
public class OpsInsightsPanelTests
{
    [Fact]
    public void Ops_insights_panel_flow_passes_via_node()
    {
        if (!NodeScriptRunner.TryResolve("node", out var node))
            return;

        var root = NodeScriptRunner.FindRepoRoot();
        Assert.True(root is not null, "未找到仓库根（LitSSHmcp.slnx）");
        var script = Path.Combine(root!, "tests", "LitSSHmcp.Web.Tests", "Frontend",
            "ops-insights.test.mjs");
        Assert.True(File.Exists(script), $"测试脚本缺失: {script}");

        var (exitCode, output, timedOut) = NodeScriptRunner.Run(node, script);
        Assert.False(timedOut, $"ops-insights.test.mjs 超时未退出\n{output}");
        Assert.True(exitCode == 0, $"ops-insights.test.mjs 失败（退出码 {exitCode}）\n{output}");
    }
}
