using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.Tests;

public class ScriptExecFormTests
{
    [Theory]
    [InlineData("bash deploy.sh", "bash", "deploy.sh")]
    [InlineData("sh /opt/app/run.sh", "sh", "/opt/app/run.sh")]
    [InlineData("python3 /srv/agent/main.py", "python3", "/srv/agent/main.py")]
    [InlineData("python2.7 legacy.py", "python2.7", "legacy.py")]
    [InlineData("powershell -File C:\\ops\\a.ps1", "powershell", "C:\\ops\\a.ps1")]
    [InlineData("pwsh -File ./deploy.ps1", "pwsh", "./deploy.ps1")]
    [InlineData("./deploy.sh", "", "./deploy.sh")]
    [InlineData("/opt/bin/x.sh", "", "/opt/bin/x.sh")]
    [InlineData("sudo bash x.sh", "bash", "x.sh")]
    [InlineData("nohup python3 worker.py", "python3", "worker.py")]
    public void Script_forms_are_detected(string command, string interpreter, string path)
    {
        Assert.True(ScriptExecForm.TryParse(command, out var i, out var p));
        Assert.Equal(interpreter, i);
        Assert.Equal(path, p);
    }

    [Theory]
    [InlineData("bash -c \"echo hi\"")]
    [InlineData("sh -lc 'df -h'")]
    [InlineData("python3 -c 'print(1)'")]
    [InlineData("ls -la")]
    [InlineData("tail -n 100 /var/log/app.log")]
    [InlineData("echo bash deploy.sh")]
    public void Inline_or_plain_commands_are_not_script_forms(string command) =>
        Assert.False(ScriptExecForm.TryParse(command, out _, out _));

    [Fact]
    public void Missing_path_after_interpreter_is_not_script_form() =>
        Assert.False(ScriptExecForm.TryParse("bash -c", out _, out _));

    [Fact]
    public void Empty_command_is_not_script_form() =>
        Assert.False(ScriptExecForm.TryParse("   ", out _, out _));
}
