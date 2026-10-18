using System.Diagnostics;

namespace LitSSHmcp.Web.Tests;

/// <summary>
/// node 脚本测试的公共底座（无 node 环境时由调用方跳过，与 mklink/junction 跳过先例一致）：
/// 解析 node 可执行文件、从测试输出目录向上找仓库根（LitSSHmcp.slnx）、限时跑脚本。
/// </summary>
internal static class NodeScriptRunner
{
    /// <summary>尝试解析 node 可执行文件（返回 false = 本机无 node）。</summary>
    public static bool TryResolve(string fileName, out string resolved)
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
    public static string? FindRepoRoot()
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

    /// <summary>限时跑 node 脚本；超时返回 TimedOut=true 并已杀进程。</summary>
    public static (int ExitCode, string Output, bool TimedOut) Run(string node, string script, int timeoutMs = 30_000)
    {
        var psi = new ProcessStartInfo(node, $"\"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动 node: {node}");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return (-1, stdout + stderr, true);
        }
        return (proc.ExitCode, stdout + stderr, false);
    }
}
