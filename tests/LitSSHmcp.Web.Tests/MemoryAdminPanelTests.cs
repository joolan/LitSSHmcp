using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>
/// 管理员记忆库前端（列表/历史/回滚/归档）：node 直跑 Frontend/memory-admin.test.mjs；无 node 时跳过。
/// </summary>
public class MemoryAdminPanelTests
{
    [Fact]
    public void Memory_admin_panel_flow_passes_via_node()
    {
        if (!NodeScriptRunner.TryResolve("node", out var node))
            return;

        var root = NodeScriptRunner.FindRepoRoot();
        Assert.True(root is not null, "未找到仓库根（LitSSHmcp.slnx）");
        var script = Path.Combine(root!, "tests", "LitSSHmcp.Web.Tests", "Frontend", "memory-admin.test.mjs");
        Assert.True(File.Exists(script), $"测试脚本缺失: {script}");

        var (exitCode, output, timedOut) = NodeScriptRunner.Run(node, script);
        Assert.False(timedOut, $"memory-admin.test.mjs 超时未退出\n{output}");
        Assert.True(exitCode == 0, $"memory-admin.test.mjs 失败（退出码 {exitCode}）\n{output}");
    }
}
