using System.Diagnostics;
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
        if (!TryResolve("node", out var node))
            return; // 无 node 环境 → 跳过

        var root = FindRepoRoot();
        Assert.True(root is not null, "未找到仓库根（LitSSHmcp.slnx）");
        var script = Path.Combine(root!, "tests", "LitSSHmcp.Web.Tests", "Frontend", "sanitize.test.mjs");
        Assert.True(File.Exists(script), $"测试脚本缺失: {script}");

        var psi = new ProcessStartInfo(node, $"\"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi);
        Assert.NotNull(proc);
        var stdout = proc!.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(30_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            Assert.Fail($"node 超时未退出\n{stdout}\n{stderr}");
        }
        Assert.True(proc.ExitCode == 0, $"sanitize.test.mjs 失败（退出码 {proc.ExitCode}）\n{stdout}\n{stderr}");
    }

    private static bool TryResolve(string fileName, out string resolved)
    {
        resolved = fileName;
        try
        {
            var psi = new ProcessStartInfo(fileName, "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(10_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从测试输出目录向上找仓库根（LitSSHmcp.slnx 标记）。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LitSSHmcp.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
