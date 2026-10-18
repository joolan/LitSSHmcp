using LitSSHmcp.Core.Services.Web;

namespace LitSSHmcp.Web;

/// <summary>
/// 首个账号引导与命令行入口（--create-user / --list-users）。
/// 首个创建的账号自动为 admin；密码只从环境变量 LITSSH_NEW_PASSWORD 读取（避免进 argv/进程列表）。
/// </summary>
public static class WebBootstrap
{
    public const string PasswordEnvVar = "LITSSH_NEW_PASSWORD";

    /// <summary>创建账号（首个自动 admin；已有账号默认 user）。返回 null 成功，否则错误文案。</summary>
    public static async Task<string?> CreateUserAsync(WebAccountsStore store, string username, string? role, string? password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password))
            return $"密码未提供：请通过环境变量 {PasswordEnvVar} 传入";
        try
        {
            var effectiveRole = role?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(effectiveRole))
                effectiveRole = await store.HasAnyUserAsync(ct) ? "user" : "admin";
            await store.CreateAsync(username, password, effectiveRole, ct: ct);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    public static async Task<int> RunCreateUserAsync(string username, string? role, CancellationToken ct = default)
    {
        var store = new WebAccountsStore();
        await store.InitializeAsync(ct);
        var error = await CreateUserAsync(store, username, role, Environment.GetEnvironmentVariable(PasswordEnvVar), ct);
        if (error is not null)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        var user = await store.FindByUsernameAsync(username, ct);
        Console.WriteLine($"已创建账号 {user!.Username}（角色 {user.Role}）");
        return 0;
    }

    public static async Task<int> RunListUsersAsync(CancellationToken ct = default)
    {
        var store = new WebAccountsStore();
        await store.InitializeAsync(ct);
        foreach (var u in await store.ListUsersAsync(ct))
        {
            var locked = u.LockedUntilUtc is { } l && l > DateTime.UtcNow ? $" 锁定至 {l:O}" : "";
            Console.WriteLine($"{u.Id}\t{u.Username}\t{u.Role}{locked}");
        }
        return 0;
    }

    /// <summary>
    /// 命令行账号管理入口（Program 启动前调用）：
    /// <c>--create-user &lt;用户名&gt; [--role admin|user]</c>（密码取环境变量 <see cref="PasswordEnvVar"/>）、
    /// <c>--list-users</c>。命中返回退出码；未命中返回 null（继续起 Web 服务，透传其余参数）。
    /// </summary>
    public static async Task<int?> RunCliAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0)
            return null;
        switch (args[0])
        {
            case "--list-users":
                return await RunListUsersAsync(ct);
            case "--create-user":
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("用法: --create-user <用户名> [--role admin|user]（密码经环境变量 " + PasswordEnvVar + " 传入）");
                    return 1;
                }
                string? role = null;
                for (var i = 2; i + 1 < args.Length; i++)
                {
                    if (args[i] == "--role")
                        role = args[i + 1];
                }
                return await RunCreateUserAsync(args[1], role, ct);
            default:
                return null;
        }
    }
}
