using LitSSHmcp.Core.Services.Web;
using Xunit;

namespace LitSSHmcp.Tests;

public class WebAccountsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public WebAccountsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "litssh-web-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "web.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录尽力清理 */ }
    }

    private WebAccountsStore NewStore() => new(_dbPath);

    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Create_and_find_roundtrip()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.CreateAsync("alice", "secret123", role: "admin", iterations: 1000);

        var user = await store.FindByUsernameAsync("ALICE");   // 大小写不敏感
        Assert.NotNull(user);
        Assert.Equal(id, user!.Id);
        Assert.Equal("alice", user.Username);
        Assert.Equal("admin", user.Role);
        Assert.Equal(0, user.FailedCount);
        Assert.Null(user.LockedUntilUtc);
        Assert.Equal(id, (await store.FindByIdAsync(id))!.Id);
        Assert.True(await store.HasAnyUserAsync());
    }

    [Fact]
    public async Task Duplicate_username_rejected()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.CreateAsync("bob", "secret123", iterations: 1000);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync("BOB", "secret234", iterations: 1000));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("has space")]
    public async Task Invalid_username_rejected(string name)
    {
        var store = NewStore();
        await store.InitializeAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(name, "secret123", iterations: 1000));
    }

    [Fact]
    public async Task Short_password_and_bad_role_rejected()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync("carol", "1234567", iterations: 1000));
        await store.CreateAsync("carol", "12345678", iterations: 1000);
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync("dave", "secret123", role: "root", iterations: 1000));
    }

    [Fact]
    public async Task Login_success_issues_validatable_token()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.CreateAsync("alice", "secret123", iterations: 1000);
        var clock = T0;
        var auth = new WebAuthService(store, () => clock);

        var outcome = await auth.LoginAsync("alice", "secret123");
        Assert.Equal(WebLoginStatus.Success, outcome.Status);
        Assert.False(string.IsNullOrEmpty(outcome.Token));
        Assert.Equal(T0 + WebAuthService.TokenLifetime, outcome.ExpiresAtUtc);

        var principal = await auth.ValidateTokenAsync(outcome.Token);
        Assert.NotNull(principal);
        Assert.Equal("alice", principal!.Username);
        Assert.False(principal.IsAdmin);   // 默认 user 角色

        var user = await store.FindByUsernameAsync("alice");
        Assert.Equal(0, user!.FailedCount);
        Assert.Equal(T0, user.LastLoginAtUtc);
    }

    [Fact]
    public async Task Login_wrong_password_fails_without_token()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.CreateAsync("alice", "secret123", iterations: 1000);
        var auth = new WebAuthService(store, () => T0);

        var outcome = await auth.LoginAsync("alice", "wrongpass");
        Assert.Equal(WebLoginStatus.InvalidCredentials, outcome.Status);
        Assert.Null(outcome.Token);
        Assert.Equal(1, (await store.FindByUsernameAsync("alice"))!.FailedCount);
    }

    [Fact]
    public async Task Login_unknown_user_returns_invalid_credentials()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var auth = new WebAuthService(store, () => T0);
        var outcome = await auth.LoginAsync("ghost", "whatever");
        Assert.Equal(WebLoginStatus.InvalidCredentials, outcome.Status);
        Assert.Null(outcome.User);
    }

    [Fact]
    public async Task Lockout_after_five_failures_and_unlock_after_expiry()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.CreateAsync("alice", "secret123", iterations: 1000);
        var clock = T0;
        var auth = new WebAuthService(store, () => clock);

        for (var i = 1; i < WebAuthPolicy.MaxFailedAttempts; i++)
            Assert.Equal(WebLoginStatus.InvalidCredentials, (await auth.LoginAsync("alice", "wrongpass")).Status);

        // 第 5 次失败：进入锁定
        var fifth = await auth.LoginAsync("alice", "wrongpass");
        Assert.Equal(WebLoginStatus.Locked, fifth.Status);
        Assert.NotNull(fifth.RetryAfter);

        // 锁定期内：即便密码正确也拒绝（先于密码校验）
        clock = T0.AddMinutes(1);
        var during = await auth.LoginAsync("alice", "secret123");
        Assert.Equal(WebLoginStatus.Locked, during.Status);
        Assert.True(during.RetryAfter > TimeSpan.Zero);

        // 过了 5 分钟锁定：正确密码可登录且清零
        clock = T0.AddMinutes(5).AddSeconds(1);
        var after = await auth.LoginAsync("alice", "secret123");
        Assert.Equal(WebLoginStatus.Success, after.Status);
        var user = await store.FindByUsernameAsync("alice");
        Assert.Equal(0, user!.FailedCount);
        Assert.Null(user.LockedUntilUtc);
    }

    [Fact]
    public async Task Expired_token_rejected_and_purged()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.CreateAsync("alice", "secret123", iterations: 1000);
        var clock = T0;
        var auth = new WebAuthService(store, () => clock);

        var outcome = await auth.LoginAsync("alice", "secret123");
        clock = clock + WebAuthService.TokenLifetime + TimeSpan.FromMinutes(1);
        Assert.Null(await auth.ValidateTokenAsync(outcome.Token));
        Assert.Null(await auth.ValidateTokenAsync(outcome.Token));   // 已删，二次也 null
    }

    [Fact]
    public async Task Logout_revokes_token()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.CreateAsync("alice", "secret123", iterations: 1000);
        var auth = new WebAuthService(store, () => T0);
        var outcome = await auth.LoginAsync("alice", "secret123");

        Assert.NotNull(await auth.ValidateTokenAsync(outcome.Token));
        await auth.LogoutAsync(outcome.Token);
        Assert.Null(await auth.ValidateTokenAsync(outcome.Token));
    }

    [Fact]
    public async Task Change_password_requires_old_and_revokes_tokens()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.CreateAsync("alice", "secret123", iterations: 1000);
        var auth = new WebAuthService(store, () => T0);
        var outcome = await auth.LoginAsync("alice", "secret123");

        Assert.Equal("旧密码不正确", await auth.ChangePasswordAsync(id, "nope", "newpass1"));

        Assert.Null(await auth.ChangePasswordAsync(id, "secret123", "newpass1"));
        Assert.Null(await auth.ValidateTokenAsync(outcome.Token));   // 改密吊销全部令牌

        Assert.Equal(WebLoginStatus.Success, (await auth.LoginAsync("alice", "newpass1")).Status);
        Assert.Equal(WebLoginStatus.InvalidCredentials, (await auth.LoginAsync("alice", "secret123")).Status);
    }

    [Fact]
    public async Task Delete_user_cascades_tokens()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.CreateAsync("alice", "secret123", iterations: 1000);
        var auth = new WebAuthService(store, () => T0);
        var outcome = await auth.LoginAsync("alice", "secret123");

        Assert.True(await store.DeleteUserAsync(id));
        Assert.Null(await auth.ValidateTokenAsync(outcome.Token));
        Assert.False(await store.HasAnyUserAsync());
    }

    [Fact]
    public void Lockout_curve_matches_policy()
    {
        Assert.Equal(TimeSpan.Zero, WebAuthPolicy.ComputeLockout(0));
        Assert.Equal(TimeSpan.Zero, WebAuthPolicy.ComputeLockout(WebAuthPolicy.MaxFailedAttempts - 1));
        Assert.Equal(WebAuthPolicy.BaseLockout, WebAuthPolicy.ComputeLockout(WebAuthPolicy.MaxFailedAttempts));
        Assert.Equal(TimeSpan.FromMinutes(10), WebAuthPolicy.ComputeLockout(WebAuthPolicy.MaxFailedAttempts + 1));
        Assert.Equal(WebAuthPolicy.MaxLockout, WebAuthPolicy.ComputeLockout(100));
    }

    [Theory]
    [InlineData("abc", true)]
    [InlineData("a_b-c.d", true)]
    [InlineData("ab", false)]
    [InlineData("_lead", false)]
    [InlineData("has space", false)]
    [InlineData("bad/slash", false)]
    public void Username_rule(string name, bool expected)
        => Assert.Equal(expected, WebAuthPolicy.IsValidUsername(name));
}
