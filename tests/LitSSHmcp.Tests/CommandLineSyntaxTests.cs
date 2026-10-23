using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.Tests;

public class CommandLineSyntaxTests
{
    [Theory]
    [InlineData("git status", CommandSyntaxError.None)]
    [InlineData("  dotnet build  ", CommandSyntaxError.None)]
    [InlineData("   ", CommandSyntaxError.None)]
    [InlineData("", CommandSyntaxError.Empty)]
    [InlineData("git commit -m \"fix: a | b; c\"", CommandSyntaxError.None)]
    [InlineData("git commit -m 'don''t run; this'", CommandSyntaxError.None)]
    [InlineData("& 'C:\\tools\\gh.exe' pr list", CommandSyntaxError.None)]
    [InlineData("git status; evil", CommandSyntaxError.CommandSeparator)]
    [InlineData("git status && evil", CommandSyntaxError.CommandSeparator)]
    [InlineData("git status &", CommandSyntaxError.CommandSeparator)]
    [InlineData("git status | more", CommandSyntaxError.Pipeline)]
    [InlineData("git log > out.txt", CommandSyntaxError.Redirection)]
    [InlineData("git log < input.txt", CommandSyntaxError.Redirection)]
    [InlineData("git log $(evil)", CommandSyntaxError.SubExpression)]
    [InlineData("git log `evil`", CommandSyntaxError.SubExpression)]
    [InlineData("git log { evil }", CommandSyntaxError.ScriptBlock)]
    [InlineData("git log (evil)", CommandSyntaxError.SubExpression)]
    [InlineData("git log [IO.File]::Delete('x')", CommandSyntaxError.SubExpression)]
    [InlineData("git log\nstatus", CommandSyntaxError.Newline)]
    [InlineData("git log\u2028status", CommandSyntaxError.UnsupportedWhitespace)]
    [InlineData("git log \"$(evil)\"", CommandSyntaxError.SubExpression)]
    [InlineData("echo 'unterminated", CommandSyntaxError.UnterminatedQuote)]
    public void Windows_single_command_reports_expected_syntax_error(string command, CommandSyntaxError expected)
        => Assert.Equal(expected, CommandLineSyntax.ValidateWindowsSingleCommand(command));

    [Theory]
    [InlineData("git status", false)]
    [InlineData("git status && evil", true)]
    [InlineData("git commit -m \"a | b\"", false)]
    [InlineData("", false)]
    public void Windows_syntax_wrapper_matches_validation(string command, bool expected)
        => Assert.Equal(expected, CommandLineSyntax.ContainsUnsafeWindowsShellSyntax(command));

    [Theory]
    [InlineData("git status", "git")]
    [InlineData("  dotnet build  ", "dotnet")]
    [InlineData("\"C:\\Program Files\\Git\\bin\\git.exe\" status", "git")]
    [InlineData("& 'C:\\tools\\gh.exe' pr list", "gh")]
    [InlineData("call aws.bat s3 ls", "aws")]
    [InlineData("", null)]
    public void First_executable_windows_resolves(string command, string? expected)
        => Assert.Equal(expected, CommandLineSyntax.FirstExecutableWindows(command));

    [Theory]
    [InlineData("call aws.bat s3 ls", new[] { "aws.bat", "s3", "ls" })]
    [InlineData("& 'C:\\tools\\gh.exe' pr list", new[] { "'C:\\tools\\gh.exe'", "pr", "list" })]
    public void Windows_tokens_skip_call_and_call_operator(string command, string[] expected)
        => Assert.Equal(expected, CommandLineSyntax.SplitWindowsTokens(command));

    [Theory]
    [InlineData("cmd", true)]
    [InlineData("PowerShell", true)]
    [InlineData("bash", true)]
    [InlineData("git", false)]
    [InlineData(null, false)]
    public void Nested_shell_is_detected(string? executable, bool expected)
        => Assert.Equal(expected, CommandLineSyntax.IsNestedShell(executable));

    [Theory]
    [InlineData("ps -ef")]
    [InlineData("echo \"a b\" c\\ d")]
    [InlineData("sh -c 'touch /tmp/x; echo ok'")]
    [InlineData("printf '' x")]
    [InlineData("echo $(evil) | tee /tmp/x")]
    public void Posix_entry_points_agree(string command)
    {
        var viaFacade = PosixCommandLine.TrySplit(command, out var facadeArgs, out var facadeError);
        var viaCore = CommandLineSyntax.TrySplitPosix(command, out var coreArgs, out var coreError);

        Assert.Equal(viaCore, viaFacade);
        Assert.Equal(coreArgs, facadeArgs);
        Assert.Equal(coreError, facadeError);
    }

    [Theory]
    [InlineData("")]
    [InlineData("echo 'unterminated")]
    [InlineData("echo trailing\\")]
    [InlineData("echo one\ntwo")]
    public void Posix_split_rejects_invalid_lines(string command)
        => Assert.False(CommandLineSyntax.TrySplitPosix(command, out _, out _));
}
