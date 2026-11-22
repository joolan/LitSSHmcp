namespace LitSSHmcp.Core.Services.Proxy;

/// <summary>单次代理连接可用的共享上下文：账号、日志、流量与请求计数。</summary>
internal sealed class ProxyRuntime
{
    private readonly object _accountGate = new();
    private readonly IReadOnlyList<ProxyAccount> _accounts;
    private readonly Action<long, long> _onTraffic;
    private readonly Action _onRequest;

    public ProxyRuntime(
        ProxyOptions options,
        IReadOnlyList<ProxyAccount> accounts,
        ProxyRequestLog log,
        Action<long, long> onTraffic,
        Action onRequest)
    {
        Options = options;
        _accounts = accounts;
        Log = log;
        _onTraffic = onTraffic;
        _onRequest = onRequest;
    }

    public ProxyOptions Options { get; }
    public ProxyRequestLog Log { get; }

    public ProxyAccount? Authenticate(string username, string password)
    {
        ProxyAccount? candidate;
        lock (_accountGate)
        {
            candidate = _accounts.FirstOrDefault(account =>
                account.Enabled &&
                string.Equals(account.Username, username, StringComparison.OrdinalIgnoreCase));
        }

        if (candidate is null || !candidate.VerifyPassword(password))
            return null;

        lock (_accountGate)
        {
            candidate.RequestCount++;
            candidate.LastUsedAt = DateTimeOffset.Now;
        }
        return candidate;
    }

    public void RecordRequest() => _onRequest();

    public void RecordTraffic(ProxyAccount? account, long bytesIn, long bytesOut)
    {
        _onTraffic(bytesIn, bytesOut);
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
}
