using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Topology;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class AssetGraphPromptFormatterTests
{
    [Fact]
    public void Empty_config_returns_null()
    {
        Assert.Null(AssetGraphPromptFormatter.Format(
            Array.Empty<SshServerConfig>(), Array.Empty<DataSourceConfig>(),
            Array.Empty<ApplicationConfig>(), Array.Empty<RelationConfig>()));
    }

    [Fact]
    public void Disabled_servers_are_excluded()
    {
        var text = AssetGraphPromptFormatter.Format(
            new[] { new SshServerConfig { Id = "s1", Name = "web01", Host = "10.0.0.1", Disabled = true } },
            null, null, null);
        Assert.Null(text);
    }

    [Fact]
    public void Renders_servers_datasources_apps_relations_with_prefixed_ids()
    {
        var text = AssetGraphPromptFormatter.Format(
            new[] { new SshServerConfig { Id = "s1", Name = "web01", Host = "10.0.0.1", Port = 22 } },
            new[] { new DataSourceConfig { Id = "d1", Name = "main", Type = "mysql", Host = "10.0.0.1", Port = 3306 } },
            new[] { new ApplicationConfig { Id = "a1", Name = "order", Type = "java", Port = 8080, Path = "/opt/app" } },
            new[] { new RelationConfig { From = "app:a1", To = "ds:d1", Type = "connectsTo" } });

        Assert.NotNull(text);
        Assert.Contains("ssh:s1", text);
        Assert.Contains("ds:d1", text);
        Assert.Contains("app:a1", text);
        Assert.Contains("app:a1 -connectsTo-> ds:d1", text);
        Assert.Contains("path=/opt/app", text);
        Assert.DoesNotContain("CreatedAt", text!);
    }

    [Fact]
    public void Ssh_tunnel_datasource_shows_tunnel_server()
    {
        var text = AssetGraphPromptFormatter.Format(
            null,
            new[] { new DataSourceConfig
            {
                Id = "d1", Name = "tun", Type = "mysql", Host = "127.0.0.1", Port = 3306,
                AccessMode = AccessMode.SshTunnel, TunnelServerId = "s9"
            } },
            null, null);

        Assert.Contains("经隧道 ssh:s9", text!);
    }

    [Fact]
    public void Overlong_is_degraded_then_truncated()
    {
        var servers = Enumerable.Range(0, 200)
            .Select(i => new SshServerConfig { Id = $"s{i}", Name = $"server-{i}", Host = $"10.0.{i / 256}.{i % 256}", Description = new string('x', 60) })
            .ToList();

        var text = AssetGraphPromptFormatter.Format(servers, null, null, null);

        Assert.NotNull(text);
        Assert.True(text!.Length <= AssetGraphPromptFormatter.MaxChars + 100);
        Assert.Contains("ssh:s0", text);
    }

    [Fact]
    public void App_details_dropped_before_truncation_when_overlong()
    {
        var longPath = "/opt/" + new string('p', 500);
        var apps = Enumerable.Range(0, 10)
            .Select(i => new ApplicationConfig { Id = $"a{i}", Name = $"app{i}", Type = "java", Path = longPath })
            .ToList();

        var text = AssetGraphPromptFormatter.Format(null, null, apps, null);

        Assert.NotNull(text);
        Assert.DoesNotContain(longPath, text!);   // includeAppDetails=false 降级：path 已被移除
    }

    [Fact]
    public void System_prompt_builder_injects_asset_section()
    {
        var prompt = LitSSHmcp.Agent.SystemPromptBuilder.Build(
            "some mcp note", null, null, assetGraph: "ssh:s1 | web01 | 10.0.0.1:22");

        Assert.Contains("## 资产清单(拓扑快照)", prompt);
        Assert.Contains("ssh:s1 | web01 | 10.0.0.1:22", prompt);
        Assert.True(prompt.IndexOf("## 资产清单", StringComparison.Ordinal)
                    < prompt.IndexOf("## MCP 服务器约定", StringComparison.Ordinal));
    }

    [Fact]
    public void System_prompt_builder_skips_asset_section_when_null()
    {
        var prompt = LitSSHmcp.Agent.SystemPromptBuilder.Build(null, null, null);
        Assert.DoesNotContain("## 资产清单", prompt);
    }
}
