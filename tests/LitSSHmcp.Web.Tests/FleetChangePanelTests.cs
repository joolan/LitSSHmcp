using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>批量变更前端（plan/canary 参数与 planId 回填）：node 直跑 Frontend/fleet-change.test.mjs。</summary>
public class FleetChangePanelTests
{
    [Fact]
    public void Fleet_change_panel_flow_passes_via_node()
    {
        Run("fleet-change.test.mjs");
    }

    [Fact]
    public void Fleet_target_picker_and_status_pass_via_node()
    {
        Run("fleet-targets.test.mjs");
    }

    private static void Run(string name)
    {
        if (!NodeScriptRunner.TryResolve("node", out var node))
            return;

        var root = NodeScriptRunner.FindRepoRoot();
        Assert.True(root is not null, "未找到仓库根（LitSSHmcp.slnx）");
        var script = Path.Combine(root!, "tests", "LitSSHmcp.Web.Tests", "Frontend", name);
        Assert.True(File.Exists(script), $"测试脚本缺失: {script}");

        var (exitCode, output, timedOut) = NodeScriptRunner.Run(node, script);
        Assert.False(timedOut, $"{name} 超时未退出\n{output}");
        Assert.True(exitCode == 0, $"{name} 失败（退出码 {exitCode}）\n{output}");
    }
}
