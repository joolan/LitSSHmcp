using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>
/// 审批面板前端逻辑（悬浮面板显隐/计数、REST 对账、注销清理）：
/// node 直跑 Frontend/approvals-panel.test.mjs（测交付给浏览器的真实 approvals.js）；无 node 跳过。
/// </summary>
public class ApprovalsPanelTests
{
    [Fact]
    public void Approval_panel_flow_passes_via_node()
    {
        if (!NodeScriptRunner.TryResolve("node", out var node))
            return; // 无 node 环境 → 跳过

        var root = NodeScriptRunner.FindRepoRoot();
        Assert.True(root is not null, "未找到仓库根（LitSSHmcp.slnx）");
        var script = Path.Combine(root!, "tests", "LitSSHmcp.Web.Tests", "Frontend", "approvals-panel.test.mjs");
        Assert.True(File.Exists(script), $"测试脚本缺失: {script}");

        var (exitCode, output, timedOut) = NodeScriptRunner.Run(node, script);
        Assert.False(timedOut, $"approvals-panel.test.mjs 超时未退出\n{output}");
        Assert.True(exitCode == 0, $"approvals-panel.test.mjs 失败（退出码 {exitCode}）\n{output}");
    }
}
