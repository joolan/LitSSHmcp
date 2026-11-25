using System.Net;
using System.Net.Sockets;
using LitSSHmcp.Core.Services.Proxy;

namespace LitSSHmcp.Core.Services.DnsServer;

/// <summary>
/// 轻量转发型 DNS 服务器配置。
/// 默认只监听本机、仅允许局域网客户端、临时开启 2 小时，定位是工具箱内的排障 / 小团队 DNS。
/// </summary>
public sealed class DnsOptions
{
    public bool Enabled { get; set; }
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 53;

    public bool UdpEnabled { get; set; } = true;
    public bool TcpEnabled { get; set; } = true;

    /// <summary>允许公网客户端。默认关闭；关闭时白名单为空仅允许回环与私有网段。</summary>
    public bool AllowPublicClients { get; set; }

    /// <summary>客户端白名单，支持 IP / CIDR；非空时优先于 <see cref="AllowPublicClients"/>。</summary>
    public List<string> AllowedClientCidrs { get; set; } = new();

    /// <summary>上游 DNS，格式：1.1.1.1、1.1.1.1:53、tcp://1.1.1.1:53、[2001:db8::1]:53；为空时使用系统 DNS。</summary>
    public List<string> Upstreams { get; set; } = new();

    public DnsCacheOptions Cache { get; set; } = new();

    public List<DnsLocalRecord> LocalRecords { get; set; } = new();

    /// <summary>域名黑名单；支持 example.com（含子域）、*.example.com 与 *。</summary>
    public List<string> BlockedDomains { get; set; } = new();

    /// <summary>域名白名单；非空时只解析白名单内的域名。</summary>
    public List<string> AllowedDomains { get; set; } = new();

    /// <summary>拦截返回方式：nxdomain 或 zero（A 返回 0.0.0.0，AAAA 返回 ::）。</summary>
    public string BlockMode { get; set; } = "nxdomain";

    /// <summary>拒绝 ANY 查询，降低被用于 DNS 放大的风险。</summary>
    public bool RefuseAny { get; set; } = true;

    public int MaxInFlight { get; set; } = 1024;
    public int MaxQpsPerClient { get; set; } = 200;
    public int MaxUdpResponseBytes { get; set; } = 1232;
    public int UpstreamTimeoutMs { get; set; } = 2000;

    /// <summary>复用上游 UDP/TCP 连接，降低高并发时的 socket 压力。</summary>
    public bool ReuseUpstreamConnections { get; set; } = true;

    /// <summary>启用响应速率限制（RRL，仅 UDP）；超限返回 TC=1，让客户端改走 TCP。</summary>
    public bool RrlEnabled { get; set; }
    public int RrlResponsesPerSecond { get; set; } = 200;
    public int RrlBurst { get; set; } = 400;

    /// <summary>临时开启分钟数；0 表示不自动关闭。默认 120 分钟。</summary>
    public int TemporaryEnableMinutes { get; set; } = 120;

    public bool LogQueries { get; set; } = true;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Port is < 0 or > 65535)
            errors.Add("DNS 端口应在 0~65535 之间（0 仅供测试使用）。");
        if (!UdpEnabled && !TcpEnabled)
            errors.Add("UDP 与 TCP 至少启用一个。");
        if (string.IsNullOrWhiteSpace(BindAddress))
            errors.Add("监听地址不能为空。");
        if (MaxInFlight is < 1 or > 65535)
            errors.Add("最大在途查询数应在 1~65535 之间。");
        if (MaxQpsPerClient is < 0 or > 100000)
            errors.Add("单客户端 QPS 上限应在 0~100000 之间（0 表示关闭限速）。");
        if (MaxUdpResponseBytes is < 512 or > 4096)
            errors.Add("UDP 响应大小应在 512~4096 字节之间。");
        if (UpstreamTimeoutMs is < 200 or > 30000)
            errors.Add("上游超时应在 200~30000 毫秒之间。");
        if (RrlResponsesPerSecond is < 1 or > 100000)
            errors.Add("RRL 每秒响应数应在 1~100000 之间。");
        if (RrlBurst is < 1 or > 200000)
            errors.Add("RRL 突发容量应在 1~200000 之间。");
        if (TemporaryEnableMinutes is < 0 or > 10080)
            errors.Add("临时开启时长应在 0~10080 分钟之间（0 表示不自动关闭）。");
        if (BlockMode is not ("nxdomain" or "zero"))
            errors.Add("拦截方式必须是 nxdomain 或 zero。");

        foreach (var value in AllowedClientCidrs)
        {
            if (!ProxyCidr.TryParse(value, out _))
                errors.Add($"客户端白名单“{value}”不是合法的 IP/CIDR。");
        }

        foreach (var value in Upstreams)
        {
            if (!DnsUpstreamEndpoint.TryParse(value, out _))
                errors.Add($"上游 DNS“{value}”格式不合法。");
        }

        foreach (var domain in (AllowedDomains ?? new List<string>())
                     .Concat(BlockedDomains ?? new List<string>()))
        {
            if (!ProxyDomainMatcher.IsValidPattern(domain))
            {
                errors.Add($"域名规则“{domain}”不合法。");
                break;
            }
        }

        if (Cache.MaxEntries is < 100 or > 1_000_000)
            errors.Add("缓存条目上限应在 100~1000000 之间。");
        if (Cache.MinTtlSeconds is < 0 or > 3600)
            errors.Add("缓存最小 TTL 应在 0~3600 秒之间。");
        if (Cache.MaxTtlSeconds is < 1 or > 604800)
            errors.Add("缓存最大 TTL 应在 1~604800 秒之间。");
        if (Cache.MaxTtlSeconds < Cache.MinTtlSeconds)
            errors.Add("缓存最大 TTL 不能小于最小 TTL。");
        if (Cache.NegativeTtlSeconds is < 0 or > 86400)
            errors.Add("负缓存 TTL 应在 0~86400 秒之间。");
        if (Cache.StaleTtlSeconds is < 1 or > 300)
            errors.Add("过期缓存返回 TTL 应在 1~300 秒之间。");
        if (Cache.StaleMaxSeconds is < 0 or > 86400)
            errors.Add("过期缓存最长保留时间应在 0~86400 秒之间。");

        foreach (var record in LocalRecords)
        {
            var error = record.Validate();
            if (error is not null)
                errors.Add(error);
        }

        return errors;
    }

    public void Normalize()
    {
        BindAddress = string.IsNullOrWhiteSpace(BindAddress) ? "127.0.0.1" : BindAddress.Trim();
        Port = Math.Clamp(Port, 0, 65535);
        MaxInFlight = Math.Clamp(MaxInFlight, 1, 65535);
        MaxQpsPerClient = Math.Clamp(MaxQpsPerClient, 0, 100000);
        MaxUdpResponseBytes = Math.Clamp(MaxUdpResponseBytes, 512, 4096);
        UpstreamTimeoutMs = Math.Clamp(UpstreamTimeoutMs, 200, 30000);
        RrlResponsesPerSecond = Math.Clamp(RrlResponsesPerSecond, 1, 100000);
        RrlBurst = Math.Clamp(RrlBurst, 1, 200000);
        TemporaryEnableMinutes = Math.Clamp(TemporaryEnableMinutes, 0, 10080);
        BlockMode = (BlockMode ?? "nxdomain").Trim().ToLowerInvariant();
        if (BlockMode is not ("nxdomain" or "zero"))
            BlockMode = "nxdomain";

        Cache ??= new DnsCacheOptions();
        Cache.Normalize();
        LocalRecords ??= new List<DnsLocalRecord>();
        foreach (var record in LocalRecords)
            record.Normalize();

        AllowedClientCidrs = (AllowedClientCidrs ?? new List<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Upstreams = (Upstreams ?? new List<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        AllowedDomains = NormalizeDomains(AllowedDomains);
        BlockedDomains = NormalizeDomains(BlockedDomains);
    }

    public DnsOptions Clone() => new()
    {
        Enabled = Enabled,
        BindAddress = BindAddress,
        Port = Port,
        UdpEnabled = UdpEnabled,
        TcpEnabled = TcpEnabled,
        AllowPublicClients = AllowPublicClients,
        AllowedClientCidrs = new List<string>(AllowedClientCidrs),
        Upstreams = new List<string>(Upstreams),
        Cache = Cache.Clone(),
        LocalRecords = LocalRecords.Select(record => record.Clone()).ToList(),
        BlockedDomains = new List<string>(BlockedDomains),
        AllowedDomains = new List<string>(AllowedDomains),
        BlockMode = BlockMode,
        RefuseAny = RefuseAny,
        MaxInFlight = MaxInFlight,
        MaxQpsPerClient = MaxQpsPerClient,
        MaxUdpResponseBytes = MaxUdpResponseBytes,
        UpstreamTimeoutMs = UpstreamTimeoutMs,
        ReuseUpstreamConnections = ReuseUpstreamConnections,
        RrlEnabled = RrlEnabled,
        RrlResponsesPerSecond = RrlResponsesPerSecond,
        RrlBurst = RrlBurst,
        TemporaryEnableMinutes = TemporaryEnableMinutes,
        LogQueries = LogQueries
    };

    private static List<string> NormalizeDomains(List<string>? values) =>
        (values ?? new List<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

public sealed class DnsCacheOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxEntries { get; set; } = 50000;
    public int MinTtlSeconds { get; set; } = 30;
    public int MaxTtlSeconds { get; set; } = 86400;
    public int NegativeTtlSeconds { get; set; } = 60;
    public bool ServeStale { get; set; } = true;
    public int StaleTtlSeconds { get; set; } = 30;
    public int StaleMaxSeconds { get; set; } = 3600;

    public void Normalize()
    {
        MaxEntries = Math.Clamp(MaxEntries, 100, 1_000_000);
        MinTtlSeconds = Math.Clamp(MinTtlSeconds, 0, 3600);
        MaxTtlSeconds = Math.Clamp(MaxTtlSeconds, 1, 604800);
        if (MaxTtlSeconds < MinTtlSeconds)
            MaxTtlSeconds = MinTtlSeconds;
        NegativeTtlSeconds = Math.Clamp(NegativeTtlSeconds, 0, 86400);
        StaleTtlSeconds = Math.Clamp(StaleTtlSeconds, 1, 300);
        StaleMaxSeconds = Math.Clamp(StaleMaxSeconds, 0, 86400);
    }

    public DnsCacheOptions Clone() => new()
    {
        Enabled = Enabled,
        MaxEntries = MaxEntries,
        MinTtlSeconds = MinTtlSeconds,
        MaxTtlSeconds = MaxTtlSeconds,
        NegativeTtlSeconds = NegativeTtlSeconds,
        ServeStale = ServeStale,
        StaleTtlSeconds = StaleTtlSeconds,
        StaleMaxSeconds = StaleMaxSeconds
    };
}

public sealed class DnsLocalRecord
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "A";
    public string Value { get; set; } = string.Empty;
    public int Ttl { get; set; } = 300;
    public bool Enabled { get; set; } = true;

    public string? Validate()
    {
        if (!DnsWire.IsValidRecordName(Name))
            return $"本地记录名称“{Name}”不合法。";
        if (Type is not ("A" or "AAAA" or "CNAME" or "PTR"))
            return $"本地记录“{Name}”类型必须是 A/AAAA/CNAME/PTR。";
        if (Ttl is < 0 or > 86400)
            return $"本地记录“{Name}”TTL 应在 0~86400 秒之间。";

        if (Type == "A")
        {
            if (!IPAddress.TryParse(Value, out var address) ||
                address.AddressFamily != AddressFamily.InterNetwork)
                return $"本地记录“{Name}”的 A 值必须是 IPv4 地址。";
        }
        else if (Type == "AAAA")
        {
            if (!IPAddress.TryParse(Value, out var address) ||
                address.AddressFamily != AddressFamily.InterNetworkV6)
                return $"本地记录“{Name}”的 AAAA 值必须是 IPv6 地址。";
        }
        else if (!DnsWire.IsValidRecordName(Value))
        {
            return $"本地记录“{Name}”的目标域名“{Value}”不合法。";
        }

        return null;
    }

    public void Normalize()
    {
        Name = DnsWire.NormalizeName(Name);
        Type = (Type ?? "A").Trim().ToUpperInvariant();
        Value = (Value ?? string.Empty).Trim();
        Ttl = Math.Clamp(Ttl, 0, 86400);
    }

    public DnsLocalRecord Clone() => new()
    {
        Name = Name,
        Type = Type,
        Value = Value,
        Ttl = Ttl,
        Enabled = Enabled
    };
}

/// <summary>上游 DNS 端点；只接受 IP 字面量，避免解析上游地址时再次依赖 DNS。</summary>
public readonly record struct DnsUpstreamEndpoint(string Protocol, IPAddress Address, int Port)
{
    public static bool TryParse(string? text, out DnsUpstreamEndpoint endpoint)
    {
        endpoint = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = text.Trim();
        var protocol = "udp";
        if (value.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
            value = value[6..];
        else if (value.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            protocol = "tcp";
            value = value[6..];
        }

        if (value.Length == 0)
            return false;

        string addressText;
        var port = 53;
        if (value[0] == '[')
        {
            var end = value.IndexOf(']');
            if (end < 0)
                return false;
            addressText = value[1..end];
            var rest = value[(end + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':' || !int.TryParse(rest[1..], out port))
                    return false;
            }
        }
        else
        {
            var colonCount = value.Count(c => c == ':');
            if (colonCount == 1)
            {
                var index = value.IndexOf(':');
                addressText = value[..index];
                if (!int.TryParse(value[(index + 1)..], out port))
                    return false;
            }
            else
            {
                // 无端口；多冒号按 IPv6 处理。
                addressText = value;
            }
        }

        if (!IPAddress.TryParse(addressText, out var address))
            return false;
        if (port is < 1 or > 65535)
            return false;

        endpoint = new DnsUpstreamEndpoint(protocol, address, port);
        return true;
    }

    public override string ToString()
    {
        var address = Address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{Address}]"
            : Address.ToString();
        return $"{Protocol}://{address}:{Port}";
    }
}
