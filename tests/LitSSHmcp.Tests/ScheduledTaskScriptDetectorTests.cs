using LitSSHmcp.Core.Services.Scheduling;
using Xunit;

namespace LitSSHmcp.Tests;

public class ScheduledTaskScriptDetectorTests
{
    [Theory]
    [InlineData("bash /opt/backup.sh", "/opt/backup.sh", "bash")]
    [InlineData("/opt/run.sh", "/opt/run.sh", "")]
    [InlineData("cd /opt/scripts && ./backup.sh --all", "/opt/scripts/backup.sh", "")]
    [InlineData("flock -n /var/lock/backup.lock /opt/backup.sh", "/opt/backup.sh", "")]
    [InlineData("python3 -u /opt/check.py", "/opt/check.py", "python3")]
    [InlineData("env FOO=1 /opt/backup.sh", "/opt/backup.sh", "")]
    [InlineData("timeout 300 /opt/backup.sh", "/opt/backup.sh", "")]
    [InlineData("nice -n 10 /opt/backup.sh", "/opt/backup.sh", "")]
    [InlineData("nohup /usr/bin/bash /opt/backup.sh >/dev/null 2>&1", "/opt/backup.sh", "bash")]
    public void Detects_script_paths(string command, string expectedPath, string expectedInterpreter)
    {
        Assert.True(ScheduledTaskScriptDetector.TryDetect(command, out var path, out var interpreter));
        Assert.Equal(expectedPath, path);
        Assert.Equal(expectedInterpreter, interpreter);
    }

    [Theory]
    [InlineData("echo hello")]
    [InlineData("bash -c '/opt/backup.sh'")]
    [InlineData("cd /opt && ls -la")]
    [InlineData("cp /opt/a /opt/b")]
    [InlineData("echo hi > /opt/out.txt")]
    [InlineData("chmod +x /opt/run.sh")]
    [InlineData("")]
    public void Ignores_commands_without_script_file(string command)
    {
        Assert.False(ScheduledTaskScriptDetector.TryDetect(command, out _, out _));
    }
}
