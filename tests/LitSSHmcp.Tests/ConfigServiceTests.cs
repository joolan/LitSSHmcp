using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.Tests;

public class ConfigServiceTests
{
    [Fact]
    public async Task Save_writes_valid_json_atomically_and_leaves_no_temp_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");

        try
        {
            var service = new ConfigService(path, new DpapiSecretProtector());
            var config = await service.LoadConfigAsync(); // 首次会创建默认配置文件
            config.Security.CommandFilter.BlockedCommands = new[] { "custom-cmd" };

            await service.SaveConfigAsync(config);

            // 原子写不应残留 .tmp 临时文件
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

            var reloaded = await service.LoadConfigAsync();
            Assert.Equal(new[] { "custom-cmd" }, reloaded.Security.CommandFilter.BlockedCommands);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Corrupt_config_throws_in_strict_mode_and_falls_back_by_default()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        await File.WriteAllTextAsync(path, "{ not valid json");

        try
        {
            var service = new ConfigService(path, new DpapiSecretProtector());

            // 普通调用保持原有容错：返回默认配置，供非安全入口继续降级运行。
            var fallback = await service.LoadConfigAsync();
            Assert.Empty(fallback.Servers);
            Assert.Empty(fallback.DataSources);

            // 应用锁等安全入口必须 fail-closed，不能把损坏配置当成“未设置密码”。
            var ex = await Assert.ThrowsAsync<ConfigLoadException>(
                () => service.LoadStrictAsync());
            Assert.Equal(path, ex.ConfigPath);
            Assert.Contains("配置文件损坏", ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Missing_config_still_creates_default_in_strict_mode()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-cfg-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "config.json");

        try
        {
            var service = new ConfigService(path, new DpapiSecretProtector());
            var config = await service.LoadStrictAsync();

            Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);
            Assert.True(File.Exists(path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Remote_desktop_password_is_encrypted_at_rest_and_round_trips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");

        try
        {
            var protector = new DpapiSecretProtector();
            var service = new ConfigService(path, protector);
            var config = await service.LoadConfigAsync();
            config.RemoteDesktops = new[]
            {
                new RemoteDesktopConfig
                {
                    Id = "rd1", Name = "研发桌面", Protocol = RemoteDesktopProtocol.Rdp,
                    Host = "10.0.0.5", Port = 3389, Username = "admin", Password = "s3cret-pw"
                }
            };

            await service.SaveConfigAsync(config);

            var raw = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("s3cret-pw", raw);        // 明文不落盘

            var reloaded = await service.LoadConfigAsync();
            var rd = Assert.Single(reloaded.RemoteDesktops);
            Assert.Equal("10.0.0.5", rd.Host);
            Assert.Equal("s3cret-pw", rd.Password);         // 解密后还原
            Assert.Equal(AppConfig.CurrentSchemaVersion, reloaded.SchemaVersion);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Mcp_server_env_and_header_secrets_are_encrypted_at_rest_and_round_trip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");

        try
        {
            var service = new ConfigService(path, new DpapiSecretProtector());
            var config = await service.LoadConfigAsync();
            config.Agent ??= new AgentConfig();
            config.Agent.McpServers = new[]
            {
                AgentMcpServerConfig.BuiltinDefault(),
                new AgentMcpServerConfig
                {
                    Id = "ext1", Name = "外部", Transport = "stdio",
                    Enabled = true,
                    Command = "npx", Args = new[] { "-y", "pkg" },
                    Env = new[] { "API_KEY=top-secret" },
                    Headers = new[] { "Authorization: Bearer hdr-secret" },
                },
                new AgentMcpServerConfig
                {
                    Id = "ext2", Name = "远程", Transport = "http", Enabled = false,
                    Url = "https://mcp.example.com/mcp",
                    Headers = new[] { "X-Api-Key: http-secret" },
                },
            };

            await service.SaveConfigAsync(config);

            var raw = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("top-secret", raw);
            Assert.DoesNotContain("hdr-secret", raw);
            Assert.DoesNotContain("http-secret", raw);

            var reloaded = await service.LoadConfigAsync();
            Assert.Equal(3, reloaded.Agent!.McpServers.Length);
            Assert.Equal("API_KEY=top-secret", reloaded.Agent.McpServers[1].Env[0]);
            Assert.Equal("Authorization: Bearer hdr-secret", reloaded.Agent.McpServers[1].Headers[0]);
            Assert.Equal("X-Api-Key: http-secret", reloaded.Agent.McpServers[2].Headers[0]);
            Assert.True(reloaded.Agent.McpServers[0].Builtin);   // 内置条目仍在第一位
            // 非密文字段保持原样
            Assert.Equal(new[] { "-y", "pkg" }, reloaded.Agent.McpServers[1].Args);
            Assert.Equal("https://mcp.example.com/mcp", reloaded.Agent.McpServers[2].Url);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Concurrent_disjoint_saves_merge_without_lost_update()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            var a = new ConfigService(path, new DpapiSecretProtector());
            var cfgA = await a.LoadConfigAsync();
            var b = new ConfigService(path, new DpapiSecretProtector());
            var cfgB = await b.LoadConfigAsync();

            cfgA.Ui ??= new UiConfig();
            cfgA.Ui.Theme = "dark";
            cfgB.Agent ??= new AgentConfig();
            cfgB.Agent.ContextLimit = 12345;

            await Task.WhenAll(a.SaveConfigAsync(cfgA), b.SaveConfigAsync(cfgB));

            var final = await new ConfigService(path, new DpapiSecretProtector()).LoadConfigAsync();
            Assert.Equal("dark", final.Ui!.Theme);
            Assert.Equal(12345, final.Agent!.ContextLimit);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Same_field_concurrent_change_throws_conflict_instead_of_overwriting()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            var a = new ConfigService(path, new DpapiSecretProtector());
            var cfgA = await a.LoadConfigAsync();
            var b = new ConfigService(path, new DpapiSecretProtector());
            var cfgB = await b.LoadConfigAsync();

            cfgA.Ui ??= new UiConfig();
            cfgA.Ui.Theme = "dark";
            cfgB.Ui ??= new UiConfig();
            cfgB.Ui.Theme = "light";

            await a.SaveConfigAsync(cfgA);
            var ex = await Assert.ThrowsAsync<ConfigConflictException>(() => b.SaveConfigAsync(cfgB));
            Assert.Contains("ui.Theme", ex.ConflictPaths, StringComparer.OrdinalIgnoreCase);

            var final = await new ConfigService(path, new DpapiSecretProtector()).LoadConfigAsync();
            Assert.Equal("dark", final.Ui!.Theme);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }
}

public class RemoteDesktopConfigTests
{
    [Fact]
    public void Default_port_depends_on_protocol()
    {
        Assert.Equal(3389, RemoteDesktopConfig.DefaultPort(RemoteDesktopProtocol.Rdp));
        Assert.Equal(5900, RemoteDesktopConfig.DefaultPort(RemoteDesktopProtocol.Vnc));
    }

    [Fact]
    public void Display_name_falls_back_to_host_port()
    {
        var cfg = new RemoteDesktopConfig { Name = "", Host = "10.0.0.5", Port = 3389 };
        Assert.Equal("10.0.0.5:3389", cfg.DisplayName);

        cfg.Name = "研发桌面";
        Assert.Equal("研发桌面", cfg.DisplayName);
    }
}
