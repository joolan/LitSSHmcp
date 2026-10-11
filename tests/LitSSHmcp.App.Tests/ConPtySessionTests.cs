using System.Text;
using LitSSHmcp.App.Services.LocalTerminal;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>ConPTY 集成测试：真实启动本机 PowerShell，验证命令执行与干净输出捕获。</summary>
public class ConPtySessionTests
{
    [Fact]
    public async Task Runs_command_and_captures_clean_output()
    {
        using var pty = new ConPtySession();
        var seen = new StringBuilder();
        pty.Output += t => { lock (seen) seen.Append(t); };

        pty.Start(100, 30);
        Assert.True(pty.IsRunning);

        var result = await pty.RunCommandAsync("Write-Output 'litssh_hello_world'", TimeSpan.FromSeconds(60));

        Assert.False(result.TimedOut);
        Assert.Contains("litssh_hello_world", result.Output);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Captures_native_command_exit_code()
    {
        using var pty = new ConPtySession();
        pty.Start(100, 30);

        // cmd /c exit 7 → 退出码应为 7
        var result = await pty.RunCommandAsync("cmd /c exit 7", TimeSpan.FromSeconds(60));

        Assert.False(result.TimedOut);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task Rejects_overlapping_command_while_busy()
    {
        using var pty = new ConPtySession();
        pty.Start(100, 30);

        var first = pty.RunCommandAsync("Start-Sleep -Seconds 3", TimeSpan.FromSeconds(30));
        await Task.Delay(800);

        var second = await pty.RunCommandAsync("Write-Output SHOULD_NOT_RUN", TimeSpan.FromSeconds(30));
        Assert.True(second.Busy);

        var firstResult = await first;
        Assert.False(firstResult.Busy);
        Assert.False(firstResult.TimedOut);
    }

    [Fact]
    public async Task Preserves_working_directory_between_commands()
    {
        using var pty = new ConPtySession();
        pty.Start(100, 30);

        var change = await pty.RunCommandAsync("Set-Location $env:SystemRoot", TimeSpan.FromSeconds(30));
        Assert.False(change.TimedOut);
        Assert.Contains("Windows", change.WorkingDirectory);

        var probe = await pty.RunCommandAsync("Write-Output (Get-Location).Path", TimeSpan.FromSeconds(30));
        Assert.Contains("Windows", probe.Output);
        Assert.Contains("Windows", probe.WorkingDirectory);
    }
}
