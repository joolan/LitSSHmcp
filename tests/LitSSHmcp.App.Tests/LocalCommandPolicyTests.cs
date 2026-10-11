using LitSSHmcp.App.Services.LocalTerminal;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class LocalCommandPolicyTests
{
    private static readonly string[] Allowed = { "git", "dotnet", "gh", "aws" };

    [Theory]
    [InlineData("git status", "git")]
    [InlineData("  dotnet build  ", "dotnet")]
    [InlineData("\"C:\\Program Files\\Git\\bin\\git.exe\" status", "git")]
    [InlineData("& 'C:\\tools\\gh.exe' pr list", "gh")]
    [InlineData("call aws.bat s3 ls", "aws")]
    public void FirstExecutable_resolves(string command, string expected)
        => Assert.Equal(expected, LocalCommandPolicy.FirstExecutable(command));

    [Theory]
    [InlineData("git status")]
    [InlineData("dotnet test")]
    [InlineData("gh pr list")]
    public void Allowlisted_commands_are_allowed(string command)
        => Assert.Equal(LocalPolicyResult.Allowed, LocalCommandPolicy.Check(command, Allowed));

    [Theory]
    [InlineData("npm install")]
    [InlineData("python -c \"print(1)\"")]
    public void Non_allowlisted_commands_are_rejected(string command)
        => Assert.Equal(LocalPolicyResult.NotInAllowlist, LocalCommandPolicy.Check(command, Allowed));

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("Remove-Item C:\\data -Recurse -Force")]
    [InlineData("shutdown /s /t 0")]
    [InlineData("reg delete HKLM\\Software\\X /f")]
    [InlineData("format c:")]
    [InlineData("")]
    [InlineData("   ")]
    public void Dangerous_or_empty_commands_are_blocked(string command)
        => Assert.Equal(LocalPolicyResult.Blocked, LocalCommandPolicy.Check(command, Allowed));

    [Theory]
    [InlineData("vim file.txt")]
    [InlineData("less app.log")]
    [InlineData("top")]
    [InlineData("watch -n 1 ls")]
    [InlineData("tail -f app.log")]
    [InlineData("tail --follow=name app.log")]
    [InlineData("python")]
    [InlineData("node")]
    [InlineData("docker exec -it web bash")]
    [InlineData("git rebase -i HEAD~3")]
    [InlineData("redis-cli")]
    public void Interactive_commands_are_rejected(string command)
        => Assert.Equal(LocalPolicyResult.Interactive, LocalCommandPolicy.Check(command, Allowed));

    [Theory]
    [InlineData("python script.py")]
    [InlineData("node app.js")]
    [InlineData("tail -n 50 app.log")]
    [InlineData("docker ps")]
    [InlineData("git log --oneline")]
    [InlineData("cmd /c exit 7")]
    public void Non_interactive_commands_are_not_flagged(string command)
        => Assert.False(LocalCommandPolicy.IsLikelyInteractive(command));
}
