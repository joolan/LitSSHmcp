using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.Tests;

public class PosixCommandLineTests
{
    [Theory]
    [InlineData("ps -ef", new[] { "ps", "-ef" })]
    [InlineData("cat /app/my file.yml", new[] { "cat", "/app/my", "file.yml" })]
    [InlineData("echo \"a b\" c\\ d", new[] { "echo", "a b", "c d" })]
    [InlineData("a\"b c\"d", new[] { "ab cd" })]
    [InlineData("sh -c 'touch /tmp/x; echo ok'", new[] { "sh", "-c", "touch /tmp/x; echo ok" })]
    [InlineData("sh -c \"echo \\\"hi\\\"\"", new[] { "sh", "-c", "echo \"hi\"" })]
    [InlineData("printf '' x", new[] { "printf", "", "x" })]
    [InlineData("true; touch /tmp/pwn", new[] { "true;", "touch", "/tmp/pwn" })]
    [InlineData("echo $(evil) | tee /tmp/x", new[] { "echo", "$(evil)", "|", "tee", "/tmp/x" })]
    public void Splits_posix_argv_without_expansion(string command, string[] expected)
    {
        Assert.True(PosixCommandLine.TrySplit(command, out var args, out var error), error);
        Assert.Equal(expected, args);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("echo 'unterminated")]
    [InlineData("echo \"unterminated")]
    [InlineData("echo trailing\\")]
    [InlineData("echo one\ntwo")]
    [InlineData("echo one\rtwo")]
    [InlineData("echo one\0two")]
    public void Rejects_invalid_command_lines(string command)
    {
        Assert.False(PosixCommandLine.TrySplit(command, out var args, out var error));
        Assert.Empty(args);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
