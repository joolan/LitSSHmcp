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
