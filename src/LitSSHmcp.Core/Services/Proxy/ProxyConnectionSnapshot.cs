using System.Net.Sockets;

namespace LitSSHmcp.Core.Services.Proxy;

/// <summary>活动连接快照（UI 展示与断开用）。</summary>
public sealed record ProxyConnectionSnapshot(
    long Id,
    string Protocol,
    string Client,
    string Username,
    string Target,
    string State,
    DateTimeOffset StartedAt,
    long BytesIn,
    long BytesOut)
{
    public string StartedText => StartedAt.ToLocalTime().ToString("HH:mm:ss");

    public string DurationText
    {
        get
        {
            var value = DateTimeOffset.Now - StartedAt;
            if (value < TimeSpan.Zero)
                value = TimeSpan.Zero;
            return value.TotalHours >= 1
                ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}"
                : $"{value.Minutes:00}:{value.Seconds:00}";
        }
    }

    public string TrafficText => $"{ProxyFormat.Bytes(BytesIn)} / {ProxyFormat.Bytes(BytesOut)}";
}

internal sealed class ProxyConnectionState : IDisposable
{
    private readonly object _gate = new();
    private readonly TcpClient _client;
    private string _username = "-";
    private string _target = "-";
    private string _state = "握手";
    private long _bytesIn;
    private long _bytesOut;

    public ProxyConnectionState(long id, string protocol, string client, string clientKey,
        TcpClient tcpClient)
    {
        Id = id;
        Protocol = protocol;
        Client = client;
        ClientKey = clientKey;
        StartedAt = DateTimeOffset.Now;
        _client = tcpClient;
    }

    public long Id { get; }
    public string Protocol { get; }
    public string Client { get; }
    public string ClientKey { get; }
    public DateTimeOffset StartedAt { get; }

    public void SetUsername(string username)
    {
        lock (_gate)
            _username = string.IsNullOrWhiteSpace(username) ? "(anonymous)" : username;
    }

    public void SetTarget(string target)
    {
        lock (_gate)
            _target = string.IsNullOrWhiteSpace(target) ? "-" : target;
    }

    public void SetState(string state)
    {
        lock (_gate)
            _state = state;
    }

    public void AddTraffic(long bytesIn, long bytesOut)
    {
        Interlocked.Add(ref _bytesIn, bytesIn);
        Interlocked.Add(ref _bytesOut, bytesOut);
    }

    public ProxyConnectionSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new ProxyConnectionSnapshot(Id, Protocol, Client, _username, _target,
                _state, StartedAt, Interlocked.Read(ref _bytesIn),
                Interlocked.Read(ref _bytesOut));
        }
    }

    public void Dispose()
    {
        try { _client.Dispose(); } catch { /* ignore */ }
    }
}

internal static class ProxyFormat
{
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.##} MB",
        >= 1024L => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} B"
    };
}
