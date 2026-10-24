using System.Net;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using Npgsql;

namespace LitSSHmcp.Core.Services.Datasource;

public interface IPostgresSession : IDisposable
{
    NpgsqlConnection Connection { get; }
    string AccessMode { get; }
    string? ViaTunnelServer { get; }
}

public interface IPostgresConnectionProvider
{
    Task<IPostgresSession> OpenAsync(DataSourceConfig ds, CancellationToken ct = default);
}

/// <summary>
/// 打开 PostgreSQL 连接：限流 + 可选 SSH 隧道 + 连接（与 <see cref="MySqlConnectionProvider"/> 同构）。
/// 密码只发给目标 PostgreSQL，不经跳板机、不写入日志。
/// </summary>
public class PostgresConnectionProvider : IPostgresConnectionProvider
{
    private readonly IConfigService _configService;
    private readonly ISshKnownHostsStore _knownHosts;
    private readonly ISecurityOptionsProvider _securityOptions;
    private readonly ITargetLimiter _targetLimiter;

    public PostgresConnectionProvider(
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

    /// <summary>按数据源配置构建连接串（含 TLS 策略）；隧道场景传 127.0.0.1 + 本地转发端口。</summary>
    public static NpgsqlConnectionStringBuilder BuildConnectionString(DataSourceConfig ds, string host, int port)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Username = ds.Username,
            Password = ds.Password ?? string.Empty,
            Database = string.IsNullOrWhiteSpace(ds.DefaultDatabase) ? "postgres" : ds.DefaultDatabase,
            Timeout = 10,
            CommandTimeout = ds.TimeoutSeconds is > 0 ? ds.TimeoutSeconds.Value : 60,
            Pooling = ds.UsePooling && ds.AccessMode != AccessMode.SshTunnel,
            SslMode = ds.SslMode switch
            {
                DataSourceSslMode.None => Npgsql.SslMode.Disable,
                DataSourceSslMode.Required => Npgsql.SslMode.Require,
                DataSourceSslMode.VerifyCA => Npgsql.SslMode.VerifyCA,
                DataSourceSslMode.VerifyFull => Npgsql.SslMode.VerifyFull,
                _ => Npgsql.SslMode.Prefer
            }
        };
        if (!string.IsNullOrWhiteSpace(ds.SslCaFile)
            && ds.SslMode is DataSourceSslMode.VerifyCA or DataSourceSslMode.VerifyFull)
            builder.RootCertificate = ds.SslCaFile;
        return builder;
    }

    public async Task<IPostgresSession> OpenAsync(DataSourceConfig ds, CancellationToken ct = default)
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

                if (server.Disabled)
                    throw new InvalidOperationException($"数据源 '{ds.Name}' {TunnelServerResolver.DisabledMessage(server)}");

                tunnel = await SshTunnel.StartAsync(server, ds.Host, ds.Port, ct, _knownHosts, _securityOptions.SshHostKey.Mode);
                connectHost = IPAddress.Loopback.ToString();
                connectPort = (int)tunnel.LocalPort;
            }

            var builder = BuildConnectionString(ds, connectHost, connectPort);
            var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(ct);

            return new PostgresSession(connection, tunnel, ds, lease);
        }
        catch
        {
            tunnel?.Dispose();
            lease?.Dispose();
            throw;
        }
    }

    private sealed class PostgresSession : IPostgresSession
    {
        private readonly SshTunnel? _tunnel;
        private readonly IDisposable? _lease;

        public NpgsqlConnection Connection { get; }
        public string AccessMode { get; }
        public string? ViaTunnelServer { get; }

        public PostgresSession(NpgsqlConnection connection, SshTunnel? tunnel, DataSourceConfig ds, IDisposable? lease)
        {
            Connection = connection;
            _tunnel = tunnel;
            _lease = lease;
            AccessMode = ds.AccessMode.ToString();
            ViaTunnelServer = tunnel?.ViaServer;
        }

        public void Dispose()
        {
            try { Connection.Dispose(); } catch { /* ignore */ }
            try { _tunnel?.Dispose(); } catch { /* ignore */ }
            try { _lease?.Dispose(); } catch { /* ignore */ }
        }
    }
}
