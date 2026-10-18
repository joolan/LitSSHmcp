using System.IO;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Web;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>
/// 桌面可选登录（评估报告 D1 收尾）：web.db 校验（与 Web 共享失败计数）、登录态 DPAPI 落盘跨重启恢复、
/// 注销/账号删除后失效、审计归因切换。用直通"加密器"替代 DPAPI，路径注入临时目录（不触真实用户数据）。
/// </summary>
public class DesktopLoginServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "litssh-desktop-login-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly string _statePath;
    private readonly PlainProtector _protector = new();

    public DesktopLoginServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "web.db");
        _statePath = Path.Combine(_dir, "desktop-login.dat");
    }

    public void Dispose()
    {
        AuditContext.CurrentUserId = null;
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Login_persists_state_and_restores_after_reload()
    {
        await CreateAccountAsync("alice", "secret123");
        var service = NewService();

        var (ok, error) = await service.LoginAsync("alice", "secret123");

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("alice", service.Current!.Username);
        service.ApplyAttribution();
        Assert.Equal("alice", AuditContext.CurrentUserId);
        Assert.True(File.Exists(_statePath));

        var reloaded = NewService();
        var restored = await reloaded.LoadAsync();
        reloaded.ApplyAttribution();

        Assert.NotNull(restored);
        Assert.Equal("alice", restored!.Username);
        Assert.Equal("alice", AuditContext.CurrentUserId);
    }

    [Fact]
    public async Task Wrong_password_fails_without_persisting()
    {
        await CreateAccountAsync("alice", "secret123");
        var service = NewService();

        var (ok, error) = await service.LoginAsync("alice", "wrong-pass");

        Assert.False(ok);
        Assert.Equal("用户名或密码错误", error);
        Assert.Null(service.Current);
        Assert.Null(AuditContext.CurrentUserId);
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public async Task Logout_clears_state_file_and_attribution()
    {
        await CreateAccountAsync("alice", "secret123");
        var service = NewService();
        await service.LoginAsync("alice", "secret123");
        service.ApplyAttribution();

        service.Logout();
        service.ApplyAttribution();

        Assert.Null(service.Current);
        Assert.Null(AuditContext.CurrentUserId);
        Assert.False(File.Exists(_statePath));
        Assert.Null(await NewService().LoadAsync());
    }

    [Fact]
    public async Task Deleted_account_invalidates_persisted_state()
    {
        var userId = await CreateAccountAsync("alice", "secret123");
        var service = NewService();
        await service.LoginAsync("alice", "secret123");
        service.ApplyAttribution();

        await new WebAccountsStore(_dbPath).DeleteUserAsync(userId);

        var reloaded = NewService();
        Assert.Null(await reloaded.LoadAsync());
        reloaded.ApplyAttribution();
        Assert.Null(AuditContext.CurrentUserId);
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public async Task Login_without_web_db_returns_friendly_error()
    {
        var service = new DesktopLoginService(Path.Combine(_dir, "missing.db"), _statePath, _protector);

        var (ok, error) = await service.LoginAsync("alice", "secret123");

        Assert.False(ok);
        Assert.Contains("尚未创建 Web 账号", error, StringComparison.Ordinal);
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public async Task Tampered_state_file_is_discarded()
    {
        await CreateAccountAsync("alice", "secret123");
        File.WriteAllText(_statePath, "not-a-valid-state");

        Assert.Null(await NewService().LoadAsync());
        Assert.False(File.Exists(_statePath));
    }

    private DesktopLoginService NewService() => new(_dbPath, _statePath, _protector);

    private async Task<long> CreateAccountAsync(string user, string password)
    {
        var store = new WebAccountsStore(_dbPath);
        await store.InitializeAsync();
        return await store.CreateAsync(user, password, "user");
    }

    /// <summary>直通加密器：确定性、不依赖 Windows 用户配置（测试隔离）。</summary>
    private sealed class PlainProtector : ISecretProtector
    {
        private const string Prefix = "test:";

        public bool IsProtected(string? value) =>
            !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

        public string? Protect(string? value) =>
            string.IsNullOrEmpty(value) ? value : Prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value!));

        public string? Unprotect(string? value) =>
            IsProtected(value) ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value![Prefix.Length..])) : value;
    }
}
