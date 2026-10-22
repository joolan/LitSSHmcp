using LitSSHmcp.App.Services.LocalTerminal;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
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

    [Fact]
    public void Default_allowlist_permits_finite_ping()
        => Assert.Equal(LocalPolicyResult.Allowed,
            LocalCommandPolicy.Check("ping -n 3 www.baidu.com", LocalTerminalConfig.DefaultAllowedCommands()));

    [Theory]
    [InlineData("python")]
    [InlineData("python3")]
    [InlineData("node")]
    [InlineData("npx")]
    [InlineData("pip")]
    [InlineData("poetry")]
    [InlineData("uv")]
    [InlineData("conda")]
    [InlineData("java")]
    public void Default_allowlist_excludes_interpreters(string exe)
        => Assert.DoesNotContain(exe, LocalTerminalConfig.DefaultAllowedCommands(),
            StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("git status && evil")]
    [InlineData("git status; evil")]
    [InlineData("git status || evil")]
    [InlineData("git status | more")]
    [InlineData("git status &")]
    [InlineData("git log > out.txt")]
    [InlineData("git log < input.txt")]
    [InlineData("git log $(evil)")]
    [InlineData("git log @(evil)")]
    [InlineData("git log `evil`")]
    [InlineData("git log { evil }")]
    [InlineData("git log (evil)")]
    [InlineData("git log [IO.File]::Delete('x')")]
    [InlineData("git log\nstatus")]
    [InlineData("git log\u2028status")]
    [InlineData("git log\u00A0status")]
    [InlineData("git log \"$(evil)\"")]
    public void Shell_syntax_is_rejected(string command)
        => Assert.Equal(LocalPolicyResult.UnsafeSyntax, LocalCommandPolicy.Check(command, Allowed));

    [Theory]
    [InlineData("git commit -m \"fix: a | b; c\"")]
    [InlineData("git commit -m 'don''t run; this'")]
    [InlineData("git log --pretty=format:'%h > %s'")]
    [InlineData("& 'C:\\tools\\gh.exe' pr list")]
    public void Quoted_literals_and_leading_call_operator_are_allowed(string command)
        => Assert.Equal(LocalPolicyResult.Allowed, LocalCommandPolicy.Check(command, Allowed));

    [Theory]
    [InlineData("cmd /c dir")]
    [InlineData("powershell -Command Get-Date")]
    [InlineData("pwsh -NoProfile -Command Get-Date")]
    public void Nested_shells_are_rejected(string command)
        => Assert.Equal(LocalPolicyResult.UnsafeSyntax, LocalCommandPolicy.Check(command, Allowed));

    [Fact]
    public void Auto_approval_requires_exact_full_command()
    {
        Assert.True(LocalCommandPolicy.IsAutoApproved("git status", new[] { "git status" }));
        Assert.False(LocalCommandPolicy.IsAutoApproved("git status --short", new[] { "git status" }));
        Assert.False(LocalCommandPolicy.IsAutoApproved("git status && evil", new[] { "git status" }));
    }

    [Fact]
    public void Auto_approval_supports_hashed_rules_without_storing_raw_secret()
    {
        const string command = "curl --password hunter2 https://example";
        var rule = AutoApprovedCommandRule.Create(command);

        Assert.True(LocalCommandPolicy.IsAutoApproved(command, new[] { rule }));
        Assert.False(LocalCommandPolicy.IsAutoApproved("curl --password other https://example", new[] { rule }));
        Assert.DoesNotContain("hunter2", rule, StringComparison.Ordinal);
    }

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
    [InlineData("ping -t 10.0.0.1")]
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

    [Theory]
    [InlineData("tail -f app.log")]
    [InlineData("npm run dev")]
    [InlineData("dotnet run")]
    [InlineData("python script.py")]
    [InlineData("docker compose up")]
    [InlineData("kubectl logs -f pod/x")]
    public void Long_running_commands_are_allowed_for_background_sessions(string command)
        => Assert.Equal(LocalPolicyResult.Allowed,
            LocalCommandPolicy.CheckForLongRunning(command, new[] { "tail", "npm", "dotnet", "python", "docker", "kubectl" }));

    [Theory]
    [InlineData("powershell")]
    [InlineData("powershell -Command Get-Date")]
    [InlineData("cmd /c dir")]
    [InlineData("python")]
    [InlineData("node")]
    [InlineData("vim app.log")]
    [InlineData("watch ls")]
    [InlineData("docker exec -it web bash")]
    [InlineData("git rebase -i HEAD~2")]
    public void Interactive_or_shell_commands_are_rejected_for_background_sessions(string command)
        => Assert.Equal(LocalPolicyResult.Interactive,
            LocalCommandPolicy.CheckForLongRunning(command, new[] { "powershell", "cmd", "python", "node", "vim", "watch", "docker", "git" }));

    [Theory]
    [InlineData("Start-Process notepad", LocalPolicyResult.Blocked)]
    [InlineData("Start-Job { Get-Date }", LocalPolicyResult.UnsafeSyntax)]
    [InlineData("npm run dev &", LocalPolicyResult.UnsafeSyntax)]
    [InlineData("rm -rf /", LocalPolicyResult.Blocked)]
    public void Background_escape_commands_are_blocked(string command, LocalPolicyResult expected)
        => Assert.Equal(expected, LocalCommandPolicy.CheckForLongRunning(command, new[] { "npm" }));

    [Fact]
    public void Background_commands_still_require_allowlist()
        => Assert.Equal(LocalPolicyResult.NotInAllowlist,
            LocalCommandPolicy.CheckForLongRunning("npm run dev", new[] { "dotnet" }));
}
