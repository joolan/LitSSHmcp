using System.Net;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using MySqlConnector;

namespace LitSSHmcp.Core.Services.Datasource;

public interface IDatasourceSession : IDisposable
{
    MySqlConnection Connection { get; }
    string AccessMode { get; }
    string? ViaTunnelServer { get; }
}

public interface IMySqlConnectionProvider
{
    Task<IDatasourceSession> OpenAsync(DataSourceConfig ds, CancellationToken ct = default);
}

public class MySqlConnectionProvider : IMySqlConnectionProvider
{
    private readonly IConfigService _configService;
    private readonly ISshKnownHostsStore _knownHosts;
    private readonly ISecurityOptionsProvider _securityOptions;
    private readonly ITargetLimiter _targetLimiter;

    public MySqlConnectionProvider(
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

    public async Task<IDatasourceSession> OpenAsync(DataSourceConfig ds, CancellationToken ct = default)
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

            var builder = new MySqlConnectionStringBuilder
            {
                Server = connectHost,
                Port = (uint)connectPort,
                UserID = ds.Username,
                Password = ds.Password ?? string.Empty,
                Database = ds.DefaultDatabase ?? string.Empty,
                ConnectionTimeout = 10,
                DefaultCommandTimeout = 60,
                Pooling = false
            };

            var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync(ct);

            return new DatasourceSession(connection, tunnel, ds, lease);
        }
        catch
        {
            tunnel?.Dispose();
            lease?.Dispose();
            throw;
        }
    }

    private sealed class DatasourceSession : IDatasourceSession
    {
        public MySqlConnection Connection { get; }
        public string AccessMode { get; }
        public string? ViaTunnelServer { get; }
        private readonly SshTunnel? _tunnel;
        private readonly IDisposable? _lease;

        public DatasourceSession(MySqlConnection connection, SshTunnel? tunnel, DataSourceConfig ds, IDisposable? lease)
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
