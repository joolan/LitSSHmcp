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
}
