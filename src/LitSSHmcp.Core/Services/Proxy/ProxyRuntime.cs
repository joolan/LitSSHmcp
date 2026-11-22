using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.Proxy;

/// <summary>单次代理连接可用的共享上下文：账号、认证失败保护、日志、流量与请求计数。</summary>
internal sealed class ProxyRuntime
{
    private readonly object _accountGate = new();
    private readonly IReadOnlyList<ProxyAccount> _accounts;
    private readonly ProxyAuthFailureTracker _authFailures;
    private readonly ProxyConnectionState _connection;
    private readonly Action<long, long> _onTraffic;
    private readonly Action _onRequest;

    public ProxyRuntime(
        ProxyOptions options,
        IReadOnlyList<ProxyAccount> accounts,
        ProxyRequestLog log,
        ProxyAuthFailureTracker authFailures,
        ProxyConnectionState connection,
        Action<long, long> onTraffic,
        Action onRequest)
    {
        Options = options;
        _accounts = accounts;
        _authFailures = authFailures;
        _connection = connection;
        Log = log;
        _onTraffic = onTraffic;
        _onRequest = onRequest;
    }

    public ProxyOptions Options { get; }
    public ProxyRequestLog Log { get; }
    public ProxyConnectionState Connection => _connection;

    public bool IsAuthBlocked(out TimeSpan retryAfter) =>
        _authFailures.IsBlocked(_connection.ClientKey, out retryAfter);

    public bool TryAuthenticate(string username, string password,
        out ProxyAccount? account, out string? error)
    {
        account = null;
        error = null;

        if (_authFailures.IsBlocked(_connection.ClientKey, out var retryAfter))
        {
            _connection.SetState("已封禁");
            error = $"认证失败次数过多，请在 {(int)Math.Ceiling(retryAfter.TotalMinutes)} 分钟后重试。";
            return false;
        }

        ProxyAccount? candidate;
        lock (_accountGate)
        {
            candidate = _accounts.FirstOrDefault(item =>
                item.Enabled &&
                string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));
        }

        if (candidate is null || !candidate.VerifyPassword(password))
        {
            var result = _authFailures.RecordFailure(_connection.ClientKey,
                Options.AuthFailureLimit, Options.AuthFailureWindowSeconds,
                Options.AuthFailureBlockSeconds);
            _connection.SetState(result.Blocked ? "已封禁" : "认证失败");
            if (result.ShouldAudit)
                AuditFailure(username, result);
            error = result.Blocked
                ? $"认证失败次数过多，已临时封禁 {(int)Math.Ceiling(result.RetryAfter.TotalMinutes)} 分钟。"
                : "用户名或密码错误。";
            return false;
        }

        _authFailures.Reset(_connection.ClientKey);
        lock (_accountGate)
        {
            candidate.RequestCount++;
            candidate.LastUsedAt = DateTimeOffset.Now;
        }
        _connection.SetUsername(candidate.Username);
        _connection.SetState("已认证");
        account = candidate;
        return true;
    }

    public void RecordRequest() => _onRequest();

    public void RecordTraffic(ProxyAccount? account, long bytesIn, long bytesOut)
    {
        _onTraffic(bytesIn, bytesOut);
        _connection.AddTraffic(bytesIn, bytesOut);
        if (account is null)
            return;
        lock (_accountGate)
        {
            account.BytesIn += bytesIn;
            account.BytesOut += bytesOut;
        }
    }

    public void AddLog(string protocol, string client, string? username,
        string target, string result, long bytesIn = 0, long bytesOut = 0)
    {
        if (!Options.LogRequests)
            return;
        Log.Add(new ProxyLogEntry(DateTimeOffset.Now, protocol, client,
            string.IsNullOrWhiteSpace(username) ? "(anonymous)" : username,
            target, result, bytesIn, bytesOut));
    }

    private void AuditFailure(string username, ProxyAuthFailureResult result)
    {
        var status = result.Blocked ? CommandStatus.Blocked : CommandStatus.Failed;
        var command = $"client={_connection.ClientKey}; username={username}; " +
                      $"failures={result.Count}; blocked={result.Blocked}";
        _ = LocalAudit.LogAsync("proxy_auth_failure", command, status,
            result: result.Blocked ? "认证失败次数达到上限，已临时封禁" : "代理认证失败");
    }
}
