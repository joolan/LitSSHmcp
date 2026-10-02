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

    [Fact]
    public void Builtin_defaults_apply_when_config_is_default_constructed()
    {
        // 反序列化时若缺少 commandFilter 段会得到 new CommandFilterConfig()，此时必须仍有内置防护（fail-closed）。
        var filter = new CommandFilterService(new FakeSecurityOptions { CommandFilter = new CommandFilterConfig() });

        Assert.Equal(CommandFilterResult.Blocked, filter.CheckCommand("docker system prune -af"));
        Assert.Equal(CommandFilterResult.Sensitive, filter.CheckCommand("chmod 777 /tmp/x"));
    }

    [Theory]
    [InlineData("xchmodz --foo")]
    [InlineData("format the disk")]
    [InlineData("echo alarm")]
    public void Substring_match_does_not_cause_false_positives(string command)
    {
        var options = new FakeSecurityOptions
        {
            CommandFilter = new CommandFilterConfig
            {
                BlockedCommands = Array.Empty<string>(),
                SensitiveCommands = new[] { "chmod", "rm" },
                SensitivePatterns = Array.Empty<string>()
            }
        };

        Assert.Equal(CommandFilterResult.Allowed, new CommandFilterService(options).CheckCommand(command));
    }

    [Fact]
    public void Multi_token_entry_matches_ignoring_extra_whitespace()
    {
        var options = new FakeSecurityOptions
        {
            CommandFilter = new CommandFilterConfig
            {
                BlockedCommands = new[] { "rm -rf /" },
                SensitiveCommands = Array.Empty<string>(),
                SensitivePatterns = Array.Empty<string>()
            }
        };

        Assert.Equal(CommandFilterResult.Blocked,
            new CommandFilterService(options).CheckCommand("sudo   rm    -rf   /var"));
    }
}
