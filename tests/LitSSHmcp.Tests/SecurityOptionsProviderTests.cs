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

    [Fact]
    public void Broken_config_is_not_cached_so_next_write_is_still_picked_up()
    {
        var path = WriteTempConfig(
            """{"security":{"commandFilter":{"blockedCommands":["A"],"sensitiveCommands":[],"sensitivePatterns":[]}}}""");
        var baseline = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, baseline);

        var provider = new SecurityOptionsProvider(path);
        Assert.True(provider.CommandFilter.IsBlocked("A"));

        // 写入损坏的 JSON，并把 mtime 前进
        var brokenTime = baseline.AddMinutes(1);
        File.WriteAllText(path, "{ this is not valid json");
        File.SetLastWriteTimeUtc(path, brokenTime);

        // 解析失败时保留上一次有效值，且不能把坏文件的 mtime 记为"已加载"
        Assert.True(provider.CommandFilter.IsBlocked("A"));

        // 用与坏文件相同的 mtime 写入有效的新配置：仍应被加载（若缓存了坏 mtime 则读不到 B）
        File.WriteAllText(path,
            """{"security":{"commandFilter":{"blockedCommands":["B"],"sensitiveCommands":[],"sensitivePatterns":[]}}}""");
        File.SetLastWriteTimeUtc(path, brokenTime);

        Assert.True(provider.CommandFilter.IsBlocked("B"));
        Assert.False(provider.CommandFilter.IsBlocked("A"));
    }
}
