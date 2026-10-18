using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>
/// sanitize.js 安全断言（评估报告 §10.B.3：模型输出 = 存储型 XSS 载体，Markdown 渲染须 sanitize）。
/// 直接用 node 跑 Frontend/sanitize.test.mjs（测的是交付给浏览器的真实文件）；
/// 本机无 node 时跳过（与 mklink/junction 测试跳过先例一致）。
/// </summary>
public class MarkdownSanitizerTests
{
    [Fact]
    public void Sanitizer_security_suite_passes_via_node()
    {
        if (!NodeScriptRunner.TryResolve("node", out var node))
            return; // 无 node 环境 → 跳过

        var root = NodeScriptRunner.FindRepoRoot();
        Assert.True(root is not null, "未找到仓库根（LitSSHmcp.slnx）");
        var script = Path.Combine(root!, "tests", "LitSSHmcp.Web.Tests", "Frontend", "sanitize.test.mjs");
        Assert.True(File.Exists(script), $"测试脚本缺失: {script}");

        var (exitCode, output, timedOut) = NodeScriptRunner.Run(node, script);
        Assert.False(timedOut, $"sanitize.test.mjs 超时未退出\n{output}");
        Assert.True(exitCode == 0, $"sanitize.test.mjs 失败（退出码 {exitCode}）\n{output}");
    }
}
