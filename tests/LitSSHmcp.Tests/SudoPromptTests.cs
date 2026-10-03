using LitSSHmcp.Core.Services.SSH;
using Xunit;

namespace LitSSHmcp.Tests;

public class SudoPromptTests
{
    [Theory]
    [InlineData("[sudo] password for alice: ", true)]
    [InlineData("Password: ", true)]
    [InlineData("password: ", true)]
    [InlineData("请输入密码:", true)]
    [InlineData("Sorry, try again.\n[sudo] password for alice:", true)]
    [InlineData("Passwort: ", true)]
    [InlineData("Mot de passe : ", true)]
    [InlineData("total 12\ndrwxr-xr-x", false)]
    [InlineData("", false)]
    public void Detects_password_prompt(string output, bool expected)
    {
        Assert.Equal(expected, SshService.LooksLikePasswordPrompt(output));
    }

    [Theory]
    [InlineData("sudo systemctl restart nginx", "systemctl restart nginx")]
    [InlineData("  sudo ls -la", "ls -la")]
    [InlineData("systemctl status nginx", "systemctl status nginx")]
    [InlineData("sudo -u deploy whoami", "sudo -u deploy whoami")]
    [InlineData("sudo", "")]
    public void Strips_leading_sudo_prefix(string command, string expected)
    {
        Assert.Equal(expected, SshService.StripSudoPrefix(command));
    }

    [Theory]
    [InlineData("sudo: sorry, you must have a tty to run sudo", true)]
    [InlineData("sudo: a terminal is required to read the password", true)]
    [InlineData("sudo: no tty present and no askpass program specified", true)]
    [InlineData("sudo: a password is required", false)]
    [InlineData("uid=0(root) gid=0(root)", false)]
    public void RequiresTty_detects_tty_required(string text, bool expected)
    {
        var result = new CommandResult { Output = text };
        Assert.Equal(expected, SshService.RequiresTty(result));
    }

    [Fact]
    public void Exit_marker_is_not_confused_by_pty_echo_of_the_command()
    {
        // pty 会回显我们输入的命令行，里面含字面量 LITSSH_EXIT:$?；不能被当成真正的标记
        var echoed = "Last login: ...\n$ su - root -c 'id'; echo LITSSH_EXIT:$?\n";
        Assert.False(SshService.TryParseExitMarker(echoed, out _));

        // 真正回传的标记（行首 + 数字）才认
        var real = echoed + "uid=0(root) gid=0(root)\nLITSSH_EXIT:0\n";
        Assert.True(SshService.TryParseExitMarker(real, out var code));
        Assert.Equal(0, code);
    }

    [Fact]
    public void Strip_exit_marker_keeps_command_output_and_ignores_echo_line()
    {
        var text = "banner\n$ echo 'LITSSH_EXIT:$?'\nhello\nLITSSH_EXIT:0\n";
        var stripped = SshService.StripExitMarker(text);
        Assert.Contains("hello", stripped);
        Assert.DoesNotContain("LITSSH_EXIT:0", stripped);
    }
}
