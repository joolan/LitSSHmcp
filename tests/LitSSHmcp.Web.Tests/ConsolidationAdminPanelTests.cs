using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>管理员记忆候选审核前端（列表/批准/拒绝）：node 直跑 Frontend/consolidation-admin.test.mjs。</summary>
public class ConsolidationAdminPanelTests
{
    [Fact]
    public void Consolidation_admin_panel_flow_passes_via_node()
    {
        if (!NodeScriptRunner.TryResolve("node", out var node))
            return;

        var root = NodeScriptRunner.FindRepoRoot();
        Assert.True(root is not null, "未找到仓库根（LitSSHmcp.slnx）");
        var script = Path.Combine(root!, "tests", "LitSSHmcp.Web.Tests", "Frontend",
            "consolidation-admin.test.mjs");
        Assert.True(File.Exists(script), $"测试脚本缺失: {script}");

        var (exitCode, output, timedOut) = NodeScriptRunner.Run(node, script);
        Assert.False(timedOut, $"consolidation-admin.test.mjs 超时未退出\n{output}");
        Assert.True(exitCode == 0, $"consolidation-admin.test.mjs 失败（退出码 {exitCode}）\n{output}");
    }
}
