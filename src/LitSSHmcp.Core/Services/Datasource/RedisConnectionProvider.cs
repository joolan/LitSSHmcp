using System.Net;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.Datasource;

public interface IRedisSession : IDisposable
{
    RedisClient Client { get; }
    string AccessMode { get; }
    string? ViaTunnelServer { get; }
}

public interface IRedisConnectionProvider
{
    Task<IRedisSession> OpenAsync(DataSourceConfig ds, CancellationToken ct = default);
}

/// <summary>
/// 打开 Redis 连接：限流 + 可选 SSH 隧道 + AUTH + SELECT，与 <see cref="MySqlConnectionProvider"/> 同构。
/// 密码（含 ACL 用户名）只发给目标 Redis，不经跳板机、不写入日志。
/// </summary>
public class RedisConnectionProvider : IRedisConnectionProvider
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly IConfigService _configService;
    private readonly ISshKnownHostsStore _knownHosts;
    private readonly ISecurityOptionsProvider _securityOptions;
    private readonly ITargetLimiter _targetLimiter;

    public RedisConnectionProvider(
        IConfigService configService,
        ISshKnownHostsStore knownHosts,
        ISecurityOptionsProvider securityOptions,
        ITargetLimiter targetLimiter)
    {
        _configService = configService;
        _knownHosts = knownHosts;
        _securityOptions = securityOptions;
        _targetLimiter = targetLimiter;
    }

    public async Task<IRedisSession> OpenAsync(DataSourceConfig ds, CancellationToken ct = default)
    {
        if (!_targetLimiter.TryAcquire($"ds:{ds.Id}", out var lease, out var limitReason))
            throw new InvalidOperationException(
                $"数据源调用被限流({limitReason})。该目标调用过于频繁或并发过高，请稍后重试。");

        SshTunnel? tunnel = null;
        var connectHost = ds.Host;
        var connectPort = ds.Port;

        try
        {
            if (ds.AccessMode == AccessMode.SshTunnel)
            {
                var config = await _configService.LoadConfigAsync();
                var server = TunnelServerResolver.Resolve(config, ds)
                    ?? throw new InvalidOperationException(
                        $"数据源 '{ds.Name}' {TunnelServerResolver.MissingServerMessage}");

                tunnel = await SshTunnel.StartAsync(server, ds.Host, ds.Port, ct, _knownHosts, _securityOptions.SshHostKey.Mode);
                connectHost = IPAddress.Loopback.ToString();
                connectPort = (int)tunnel.LocalPort;
            }

            var client = await RedisClient.ConnectAsync(connectHost, connectPort, ConnectTimeout, ct);

            try
            {
                await HandshakeAsync(client, ds, ct);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            return new RedisSession(client, tunnel, ds, lease);
        }
        catch
        {
            tunnel?.Dispose();
            lease?.Dispose();
            throw;
        }
    }

    private static async Task HandshakeAsync(RedisClient client, DataSourceConfig ds, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(ds.Password))
        {
            var args = string.IsNullOrEmpty(ds.Username)
                ? new[] { "AUTH", ds.Password! }
                : new[] { "AUTH", ds.Username, ds.Password! };

            var reply = await client.ExecuteAsync(args, ct);
            if (reply.IsError)
                throw new InvalidOperationException($"Redis 认证失败: {reply.ErrorMessage}");
        }

        // DefaultDatabase 对 Redis 表示 DB 索引（0..15），留空则用 DB0
        if (!string.IsNullOrWhiteSpace(ds.DefaultDatabase) &&
            int.TryParse(ds.DefaultDatabase.Trim(), out var db) && db >= 0)
        {
            var reply = await client.ExecuteAsync(new[] { "SELECT", db.ToString() }, ct);
            if (reply.IsError)
                throw new InvalidOperationException($"Redis SELECT {db} 失败: {reply.ErrorMessage}");
        }
    }

    private sealed class RedisSession : IRedisSession
    {
        private readonly SshTunnel? _tunnel;
        private readonly IDisposable? _lease;

        public RedisClient Client { get; }
        public string AccessMode { get; }
        public string? ViaTunnelServer { get; }

        public RedisSession(RedisClient client, SshTunnel? tunnel, DataSourceConfig ds, IDisposable? lease)
        {
            Client = client;
            _tunnel = tunnel;
            _lease = lease;
            AccessMode = ds.AccessMode.ToString();
            ViaTunnelServer = tunnel?.ViaServer;
        }

        public void Dispose()
        {
            try { Client.Dispose(); } catch { /* ignore */ }
            try { _tunnel?.Dispose(); } catch { /* ignore */ }
            try { _lease?.Dispose(); } catch { /* ignore */ }
        }
    }
}
