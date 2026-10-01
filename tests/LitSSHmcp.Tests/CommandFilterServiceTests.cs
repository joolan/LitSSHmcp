using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.Tests;

public class CommandFilterServiceTests
{
    private static CommandFilterService Create()
    {
        var options = new FakeSecurityOptions
        {
            CommandFilter = new CommandFilterConfig
            {
                BlockedCommands = new[] { "rm -rf /" },
                SensitiveCommands = new[] { "rm " },
                SensitivePatterns = new[] { @"\breboot\b" }
            }
        };
        return new CommandFilterService(options);
    }

    [Fact]
    public void Blocked_command_is_blocked()
    {
        Assert.Equal(CommandFilterResult.Blocked, Create().CheckCommand("rm -rf /"));
    }

    [Fact]
    public void Sensitive_command_is_sensitive()
    {
        Assert.Equal(CommandFilterResult.Sensitive, Create().CheckCommand("rm /tmp/x"));
    }

    [Fact]
    public void Sensitive_pattern_is_sensitive()
    {
        Assert.Equal(CommandFilterResult.Sensitive, Create().CheckCommand("sudo reboot"));
    }

    [Fact]
    public void Normal_command_is_allowed()
    {
        Assert.Equal(CommandFilterResult.Allowed, Create().CheckCommand("ls -la"));
    }
}
