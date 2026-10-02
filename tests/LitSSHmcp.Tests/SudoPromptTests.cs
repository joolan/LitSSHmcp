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
}
