using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Resources;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

/// <summary>验证 MCP Resources：只读、可复用、**不泄露密码**、禁用服务器排除、指南可用。</summary>
public class ResourceTests
{
    [Fact]
    public async Task Servers_resource_excludes_disabled_and_secrets()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "s1", Name = "web", Host = "10.0.0.1", Port = 22, Username = "root", Password = "SECRET-PW" },
                new SshServerConfig { Id = "s2", Name = "off", Host = "10.0.0.2", Password = "SECRET2", Disabled = true }
            }
        };

        var json = await new SshResources(new StubConfig(config)).GetServers();

        Assert.Contains("10.0.0.1", json);
        Assert.Contains("\"s1\"", json);
        Assert.DoesNotContain("SECRET-PW", json);
        Assert.DoesNotContain("SECRET2", json);
        Assert.DoesNotContain("10.0.0.2", json);   // 禁用服务器被排除
    }

    [Fact]
    public async Task Datasources_resource_has_no_password()
    {
        var config = new AppConfig
        {
            DataSources = new[]
            {
                new DataSourceConfig { Id = "d1", Name = "db", Type = "mysql", Host = "10.0.0.1", Port = 3306, Username = "u", Password = "SECRET-DB" }
            }
        };

        var json = await new DatasourceResources(new StubConfig(config)).GetDatasources();

        Assert.Contains("\"d1\"", json);
        Assert.DoesNotContain("SECRET-DB", json);
    }

    [Fact]
    public async Task Guide_resource_returns_group_aware_guide()
    {
        var text = await new GuideResources(new StubConfig(new AppConfig())).GetGuide();

        Assert.Contains("LitSSH MCP 使用指南", text);
        Assert.Contains("tools", text);
    }

    private sealed class StubConfig : IConfigService
    {
        private readonly AppConfig _config;
        public StubConfig(AppConfig config) => _config = config;
        public Task<AppConfig> LoadConfigAsync() => Task.FromResult(_config);
        public Task SaveConfigAsync(AppConfig config) => Task.CompletedTask;
        public string GetConfigPath() => string.Empty;
    }
}
