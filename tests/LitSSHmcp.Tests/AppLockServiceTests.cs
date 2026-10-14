using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.Tests;

public class AppLockServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "litssh-applock-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly ConfigService _configService;

    public AppLockServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "config.json");
        _configService = new ConfigService(_path, new DpapiSecretProtector());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    private AppLockService CreateService() => new(_configService);

    [Fact]
    public async Task Fresh_config_is_not_configured()
    {
        var config = await _configService.LoadConfigAsync();
        Assert.False(AppLockService.IsConfigured(config.AppLock));
        Assert.False(await CreateService().IsEnabledAsync());
    }

    [Fact]
    public async Task SetPassword_enables_and_verifies()
    {
        var service = CreateService();
        await service.SetPasswordAsync("s3cret-pass");

        Assert.True(await service.IsEnabledAsync());
        Assert.True(await service.VerifyAsync("s3cret-pass"));
        Assert.False(await service.VerifyAsync("wrong-pass"));
        Assert.False(await service.VerifyAsync(""));
    }

    [Fact]
    public async Task Password_hash_is_protected_at_rest_and_plaintext_absent()
    {
        await CreateService().SetPasswordAsync("s3cret-pass");

        var raw = await File.ReadAllTextAsync(_path);
        Assert.DoesNotContain("s3cret-pass", raw);
        Assert.Contains("enc:", raw);   // 哈希/盐经 DPAPI 加密后落盘
    }

    [Fact]
    public async Task Verify_works_across_service_instances()
    {
        await CreateService().SetPasswordAsync("s3cret-pass");

        // 新实例（模拟重启后重新加载配置）仍能校验
        var fresh = new AppLockService(new ConfigService(_path, new DpapiSecretProtector()));
        Assert.True(await fresh.VerifyAsync("s3cret-pass"));
    }

    [Fact]
    public async Task Short_password_throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CreateService().SetPasswordAsync("123"));
    }

    [Fact]
    public async Task Disable_clears_hash_and_salt()
    {
        var service = CreateService();
        await service.SetPasswordAsync("s3cret-pass");
        await service.DisableAsync();

        Assert.False(await service.IsEnabledAsync());
        Assert.False(await service.VerifyAsync("s3cret-pass"));

        var config = await _configService.LoadConfigAsync();
        Assert.False(AppLockService.IsConfigured(config.AppLock));
    }

    [Fact]
    public async Task Tampered_hash_returns_false_instead_of_throwing()
    {
        await CreateService().SetPasswordAsync("s3cret-pass");

        var config = await _configService.LoadConfigAsync();
        config.AppLock!.PasswordHash = "!!!not-base64!!!";
        await _configService.SaveConfigAsync(config);

        Assert.False(await CreateService().VerifyAsync("s3cret-pass"));
    }

    [Fact]
    public async Task Changing_password_invalidates_old_one()
    {
        var service = CreateService();
        await service.SetPasswordAsync("old-pass-1");
        await service.SetPasswordAsync("new-pass-2");

        Assert.False(await service.VerifyAsync("old-pass-1"));
        Assert.True(await service.VerifyAsync("new-pass-2"));
    }
}
