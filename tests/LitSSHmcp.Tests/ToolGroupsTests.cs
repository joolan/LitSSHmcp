using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.Tests;

public class ToolGroupsTests
{
    [Fact]
    public void Deserializes_tools_enabled_groups_from_camel_case_json()
    {
        var cfg = JsonSerializer.Deserialize<AppConfig>(
            "{\"schemaVersion\":1,\"tools\":{\"enabledGroups\":[\"ssh\",\"guide\"]}}", AppConfigJson.Options)!;
        Assert.Equal(new[] { "ssh", "guide" }, cfg.Tools.EnabledGroups);
    }

    [Fact]
    public async Task Config_service_round_trips_tool_groups()
    {
        var path = Path.Combine(Path.GetTempPath(), "litssh-tools-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var service = new ConfigService(path, new DpapiSecretProtector());
            await service.SaveConfigAsync(new AppConfig
            {
                Tools = new ToolsConfig { EnabledGroups = new[] { "ssh", "guide" } }
            });

            var loaded = await service.LoadConfigAsync();
            Assert.Equal(new[] { "ssh", "guide" }, loaded.Tools.EnabledGroups);
            Assert.Equal(2, ToolGroups.ResolveEnabled(loaded.Tools).Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Null_or_empty_means_all_groups()
    {
        Assert.Equal(ToolGroups.All.Length, ToolGroups.ResolveEnabled(null).Count);
        Assert.Equal(ToolGroups.All.Length, ToolGroups.ResolveEnabled(new ToolsConfig()).Count);
        Assert.Equal(ToolGroups.All.Length, ToolGroups.ResolveEnabled(new ToolsConfig { EnabledGroups = Array.Empty<string>() }).Count);
    }

    [Fact]
    public void All_keyword_means_all_groups()
    {
        Assert.Equal(ToolGroups.All.Length, ToolGroups.ResolveEnabled(new ToolsConfig { EnabledGroups = new[] { "all" } }).Count);
        Assert.Equal(ToolGroups.All.Length, ToolGroups.ResolveEnabled(new ToolsConfig { EnabledGroups = new[] { "ALL", "mysql" } }).Count);
    }

    [Fact]
    public void Subset_resolves_to_listed_groups()
    {
        var enabled = ToolGroups.ResolveEnabled(new ToolsConfig { EnabledGroups = new[] { "mysql", "redis" } });
        Assert.Equal(2, enabled.Count);
        Assert.Contains(ToolGroups.Mysql, enabled);
        Assert.Contains(ToolGroups.Redis, enabled);
        Assert.DoesNotContain(ToolGroups.Ssh, enabled);
    }

    [Fact]
    public void Group_matching_is_case_insensitive()
    {
        var enabled = ToolGroups.ResolveEnabled(new ToolsConfig { EnabledGroups = new[] { "MySQL", "FILEtransfer" } });
        Assert.Contains(ToolGroups.Mysql, enabled);
        Assert.Contains(ToolGroups.FileTransfer, enabled);
        Assert.Equal(2, enabled.Count);
    }

    [Fact]
    public void None_keyword_means_no_groups()
    {
        Assert.Empty(ToolGroups.ResolveEnabled(new ToolsConfig { EnabledGroups = new[] { "none" } }));
        Assert.Empty(ToolGroups.UnknownGroups(new ToolsConfig { EnabledGroups = new[] { "none" } }));
        Assert.Equal(ToolGroups.All.Length, ToolGroups.ResolveEnabled(new ToolsConfig { EnabledGroups = new[] { "none", "all" } }).Count);
    }

    [Fact]
    public void Unknown_groups_are_ignored_and_reported()
    {
        var config = new ToolsConfig { EnabledGroups = new[] { "mysql", "bogus" } };
        var enabled = ToolGroups.ResolveEnabled(config);
        Assert.Equal(new[] { ToolGroups.Mysql }, enabled.ToArray());
        Assert.Equal(new[] { "bogus" }, ToolGroups.UnknownGroups(config));
    }

    [Fact]
    public void Unknown_detection_excludes_missing_config_and_all_keyword()
    {
        Assert.Empty(ToolGroups.UnknownGroups(null));
        Assert.Empty(ToolGroups.UnknownGroups(new ToolsConfig()));
        Assert.Empty(ToolGroups.UnknownGroups(new ToolsConfig { EnabledGroups = new[] { "all" } }));
        Assert.Equal(new[] { "nope" }, ToolGroups.UnknownGroups(new ToolsConfig { EnabledGroups = new[] { "all", "nope" } }));
    }
}
