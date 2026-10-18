using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>
/// 前端聊天输入框启用逻辑（登录后选中会话才能输入、无会话/运行中正确禁用）。
/// 直接用 node 跑 Frontend/chat-open.test.mjs（测的是交付给浏览器的真实 chat.js）；
/// 本机无 node 时跳过（与 sanitize 套件同一先例）。
/// </summary>
public class ChatInputTests
{
    [Fact]
    public void Chat_input_enable_flow_passes_via_node()
    {
        if (!NodeScriptRunner.TryResolve("node", out var node))
            return; // 无 node 环境 → 跳过

        var root = NodeScriptRunner.FindRepoRoot();
        Assert.True(root is not null, "未找到仓库根（LitSSHmcp.slnx）");
        var script = Path.Combine(root!, "tests", "LitSSHmcp.Web.Tests", "Frontend", "chat-open.test.mjs");
        Assert.True(File.Exists(script), $"测试脚本缺失: {script}");

        var (exitCode, output, timedOut) = NodeScriptRunner.Run(node, script);
        Assert.False(timedOut, $"chat-open.test.mjs 超时未退出\n{output}");
        Assert.True(exitCode == 0, $"chat-open.test.mjs 失败（退出码 {exitCode}）\n{output}");
    }
}
