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
}
