using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.Tests;

public class SecurityOptionsProviderTests
{
    private static string WriteTempConfig(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Reads_command_filter_from_config()
    {
        var path = WriteTempConfig(
            """{"security":{"commandFilter":{"blockedCommands":["foo"],"sensitiveCommands":[],"sensitivePatterns":[]}}}""");

        var provider = new SecurityOptionsProvider(path);

        Assert.True(provider.CommandFilter.IsBlocked("foo"));
    }

    [Fact]
    public void Missing_config_falls_back_to_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "litssh-missing-" + Guid.NewGuid().ToString("N") + ".json");

        var provider = new SecurityOptionsProvider(path);

        Assert.NotNull(provider.CommandFilter);
        Assert.NotNull(provider.SqlFilter);
        Assert.NotNull(provider.FileTransfer);
    }

    [Fact]
    public void Config_change_is_picked_up_without_restart()
    {
        var path = WriteTempConfig(
            """{"security":{"commandFilter":{"blockedCommands":["foo"],"sensitiveCommands":[],"sensitivePatterns":[]}}}""");

        var provider = new SecurityOptionsProvider(path);
        Assert.True(provider.CommandFilter.IsBlocked("foo"));

        File.WriteAllText(path,
            """{"security":{"commandFilter":{"blockedCommands":["bar"],"sensitiveCommands":[],"sensitivePatterns":[]}}}""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.False(provider.CommandFilter.IsBlocked("foo"));
        Assert.True(provider.CommandFilter.IsBlocked("bar"));
    }
}
