using LitSSHmcp.Core.Services.Web;
using LitSSHmcp.Web;
using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>
/// 账号管理命令行入口（M3-1 账号体系的运维入口：--create-user/--list-users，§3.2）。
/// 密码只经环境变量传入；首个账号自动 admin。库用临时路径隔离。
/// </summary>
public class WebBootstrapCliTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), "litssh-users-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* 清理失败忽略 */ }
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Non_cli_args_pass_through_to_host()
    {
        Assert.Null(await WebBootstrap.RunCliAsync(Array.Empty<string>()));
        Assert.Null(await WebBootstrap.RunCliAsync(new[] { "--urls", "http://localhost:5000" }));
        Assert.Null(await WebBootstrap.RunCliAsync(new[] { "--unknown-flag" }));
    }

    [Fact]
    public async Task Create_user_requires_username_and_password_env()
    {
        var previous = Environment.GetEnvironmentVariable(WebBootstrap.PasswordEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(WebBootstrap.PasswordEnvVar, null);
            Assert.Equal(1, await WebBootstrap.RunCliAsync(new[] { "--create-user" }));
            // 用户名给了但密码环境变量缺失 → 明确报错退出码 1
            var store = new WebAccountsStore(_dbPath);
            await store.InitializeAsync();
            var error = await WebBootstrap.CreateUserAsync(store, "nopass", role: null, password: null);
            Assert.NotNull(error);
            Assert.Contains(WebBootstrap.PasswordEnvVar, error, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WebBootstrap.PasswordEnvVar, previous);
        }
    }

    [Fact]
    public async Task First_created_user_is_admin_then_default_user()
    {
        var store = new WebAccountsStore(_dbPath);
        await store.InitializeAsync();

        Assert.Null(await WebBootstrap.CreateUserAsync(store, "boss", role: null, password: "pw123456"));
        var first = await store.FindByUsernameAsync("boss");
        Assert.Equal("admin", first!.Role);

        Assert.Null(await WebBootstrap.CreateUserAsync(store, "worker", role: null, password: "pw123456"));
        Assert.Equal("user", (await store.FindByUsernameAsync("worker"))!.Role);

        Assert.Null(await WebBootstrap.CreateUserAsync(store, "boss2", role: "admin", password: "pw123456"));
        Assert.Equal("admin", (await store.FindByUsernameAsync("boss2"))!.Role);

        // 重名 → 返回错误文案（不抛）
        var dup = await WebBootstrap.CreateUserAsync(store, "boss", role: null, password: "pw123456");
        Assert.NotNull(dup);
    }

    [Fact]
    public async Task List_users_exits_zero()
    {
        Assert.Equal(0, await WebBootstrap.RunCliAsync(new[] { "--list-users" }));
    }
}
