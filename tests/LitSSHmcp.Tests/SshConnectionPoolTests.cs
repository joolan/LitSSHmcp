using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using Renci.SshNet;
using Xunit;

namespace LitSSHmcp.Tests;

/// <summary>
/// 连接池容量等待的回归测试：
/// 满员时必须有界等待（AcquireTimeoutSeconds 超时抛 SshConnectionPoolBusyException），
/// 取消令牌必须能及时中断等待，而不是无限挂起。
/// </summary>
public class SshConnectionPoolTests
{
    private static ConnectionPoolConfig Options(int maxPerServer = 1, int acquireTimeoutSeconds = 1) =>
        new() { Enabled = true, MaxPerServer = maxPerServer, AcquireTimeoutSeconds = acquireTimeoutSeconds, ConnectTimeoutSeconds = 5 };

    private static SshConnectionPool CreatePool(ConnectionPoolConfig options) =>
        new(() => options,
            _ => new SshClient(new ConnectionInfo("h", 22, "u", new PasswordAuthenticationMethod("u", "p"))),
            _ => new SftpClient(new ConnectionInfo("h", 22, "u", new PasswordAuthenticationMethod("u", "p"))),
            connector: (_, _) => { });   // 假连接：不做真实 Connect

    private static SshServerConfig Server() => new() { Id = "s1", Name = "s1", Host = "h", Port = 22, Username = "u", AuthType = AuthType.Password, Password = "p" };

    [Fact]
    public void Pool_full_times_out_with_busy_exception_instead_of_hanging()
    {
        using var pool = CreatePool(Options(maxPerServer: 1, acquireTimeoutSeconds: 1));
        var server = Server();

        using var first = pool.Rent(server);   // 占满唯一容量

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<SshConnectionPoolBusyException>(() => pool.Rent(server));
        sw.Stop();

        Assert.Contains("连接池繁忙", ex.Message);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(800), $"等待过短: {sw.Elapsed}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"超时未生效: {sw.Elapsed}");
    }

    [Fact]
    public void Pool_full_wait_is_interrupted_by_cancellation()
    {
        using var pool = CreatePool(Options(maxPerServer: 1, acquireTimeoutSeconds: 60));
        var server = Server();

        using var first = pool.Rent(server);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => pool.Rent(server, cts.Token));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"取消未及时生效: {sw.Elapsed}");
    }

    [Fact]
    public void Released_slot_is_reusable()
    {
        using var pool = CreatePool(Options(maxPerServer: 1, acquireTimeoutSeconds: 2));
        var server = Server();

        var first = pool.Rent(server);
        first.Dispose();

        using var second = pool.Rent(server);   // 归还后应立即可租
        Assert.NotNull(second.Client);
    }

    [Fact]
    public void Busy_exception_on_sftp_channel_too()
    {
        using var pool = CreatePool(Options(maxPerServer: 1, acquireTimeoutSeconds: 1));
        var server = Server();

        using var first = pool.RentSftp(server);
        Assert.Throws<SshConnectionPoolBusyException>(() => pool.RentSftp(server));
    }
}
