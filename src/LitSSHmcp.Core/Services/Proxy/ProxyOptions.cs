using System.Text.Json.Serialization;

namespace LitSSHmcp.Core.Services.Proxy;

/// <summary>
/// 局域网 HTTP / SOCKS5 代理监听与访问策略。
/// 默认只监听本机、要求账号、只允许 80/443、禁止私有目标、临时开启 2 小时。
/// </summary>
public sealed class ProxyOptions
{
    public bool Enabled { get; set; }
    public string BindAddress { get; set; } = "127.0.0.1";

    public bool HttpEnabled { get; set; } = true;
    public int HttpPort { get; set; } = 8888;

    public bool Socks5Enabled { get; set; } = true;
    public int Socks5Port { get; set; } = 1080;

    /// <summary>匿名模式：默认关闭。开启后仍执行客户端白名单与目标策略。</summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>客户端白名单，支持 IP、CIDR；空表示不限制。</summary>
    public List<string> AllowedClientCidrs { get; set; } = new();

    /// <summary>允许访问的目标端口；<see cref="AllowAllPorts"/> 为 true 时忽略。</summary>
    public List<int> AllowedPorts { get; set; } = new() { 80, 443 };

    public bool AllowAllPorts { get; set; }

    /// <summary>禁止访问回环/私有/链路本地/保留地址；排障时可关闭。</summary>
    public bool BlockPrivateDestinations { get; set; } = true;

    /// <summary>域名白名单；为空表示不限制。支持 example.com（含子域）、*.example.com 与 *。</summary>
    public List<string> AllowedDomains { get; set; } = new();

    /// <summary>域名黑名单；优先于白名单。支持与白名单相同的匹配规则。</summary>
    public List<string> BlockedDomains { get; set; } = new();

    /// <summary>同一客户端 IP 在窗口内允许的认证失败次数；0 表示关闭限速。</summary>
    public int AuthFailureLimit { get; set; } = 10;
    public int AuthFailureWindowSeconds { get; set; } = 300;
    public int AuthFailureBlockSeconds { get; set; } = 300;

    /// <summary>上游代理链：先经该 HTTP/SOCKS5 代理再访问目标。</summary>
    public bool UpstreamProxyEnabled { get; set; }
    public string UpstreamProxyType { get; set; } = "http";
    public string UpstreamProxyHost { get; set; } = string.Empty;
    public int UpstreamProxyPort { get; set; }
    public string UpstreamProxyUsername { get; set; } = string.Empty;

    /// <summary>DPAPI 保护后的上游代理密码；运行时明文见 <see cref="UpstreamProxyPassword"/>。</summary>
    public string UpstreamProxyPasswordProtected { get; set; } = string.Empty;

    [JsonIgnore]
    public string UpstreamProxyPassword { get; set; } = string.Empty;

    public int MaxTotalConnections { get; set; } = 128;
    public int MaxConnectionsPerClient { get; set; } = 32;
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int HeaderTimeoutSeconds { get; set; } = 10;
    public int IdleTimeoutSeconds { get; set; } = 120;
    public int MaxConnectionMinutes { get; set; } = 30;

    /// <summary>临时开启分钟数；0 表示不自动关闭。默认 120 分钟。</summary>
    public int TemporaryEnableMinutes { get; set; } = 120;

    public bool LogRequests { get; set; } = true;

    /// <summary>返回校验错误；为空表示可以启动。</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!HttpEnabled && !Socks5Enabled)
            errors.Add("HTTP 与 SOCKS5 至少启用一个。");
        if (HttpEnabled)
            ValidatePort(HttpPort, "HTTP 端口", errors);
        if (Socks5Enabled)
            ValidatePort(Socks5Port, "SOCKS5 端口", errors);
        if (HttpEnabled && Socks5Enabled && HttpPort != 0 && HttpPort == Socks5Port)
            errors.Add("HTTP 与 SOCKS5 不能使用同一个端口。");
        if (string.IsNullOrWhiteSpace(BindAddress))
            errors.Add("监听地址不能为空。");
        if (MaxTotalConnections is < 1 or > 4096)
            errors.Add("总并发连接数应在 1~4096 之间。");
        if (MaxConnectionsPerClient is < 1 or > 1024)
            errors.Add("单 IP 并发连接数应在 1~1024 之间。");
        if (MaxConnectionsPerClient > MaxTotalConnections)
            errors.Add("单 IP 并发不能大于总并发。");
        if (ConnectTimeoutSeconds is < 1 or > 120)
            errors.Add("连接超时应在 1~120 秒之间。");
        if (HeaderTimeoutSeconds is < 1 or > 120)
            errors.Add("请求头超时应在 1~120 秒之间。");
        if (IdleTimeoutSeconds is < 5 or > 3600)
            errors.Add("空闲超时应在 5~3600 秒之间。");
        if (MaxConnectionMinutes is < 1 or > 1440)
            errors.Add("单连接最长时长应在 1~1440 分钟之间。");
        if (TemporaryEnableMinutes is < 0 or > 10080)
            errors.Add("临时开启时长应在 0~10080 分钟之间（0 表示不自动关闭）。");
        if (AuthFailureLimit is < 0 or > 1000)
            errors.Add("认证失败次数上限应在 0~1000 之间（0 表示关闭限速）。");
        if (AuthFailureWindowSeconds is < 30 or > 86400)
            errors.Add("认证失败统计窗口应在 30~86400 秒之间。");
        if (AuthFailureBlockSeconds is < 30 or > 86400)
            errors.Add("认证失败封禁时长应在 30~86400 秒之间。");
        if (!AllowAllPorts && AllowedPorts.Count == 0)
            errors.Add("目标端口允许列表为空；请添加端口或允许所有端口。");
        foreach (var port in AllowedPorts)
        {
            if (port is < 1 or > 65535)
            {
                errors.Add($"目标端口 {port} 不合法。");
                break;
            }
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
        if (UpstreamProxyEnabled)
        {
            if (UpstreamProxyType is not ("http" or "socks5"))
                errors.Add("上游代理类型必须是 http 或 socks5。");
            if (string.IsNullOrWhiteSpace(UpstreamProxyHost))
                errors.Add("启用上游代理时，上游地址不能为空。");
            if (UpstreamProxyPort is < 1 or > 65535)
                errors.Add("启用上游代理时，上游端口应在 1~65535 之间。");
        }

        return errors;
    }

    public void Normalize()
    {
        BindAddress = string.IsNullOrWhiteSpace(BindAddress) ? "127.0.0.1" : BindAddress.Trim();
        HttpPort = Math.Clamp(HttpPort, 0, 65535);
        Socks5Port = Math.Clamp(Socks5Port, 0, 65535);
        MaxTotalConnections = Math.Clamp(MaxTotalConnections, 1, 4096);
        MaxConnectionsPerClient = Math.Clamp(MaxConnectionsPerClient, 1, 1024);
        if (MaxConnectionsPerClient > MaxTotalConnections)
            MaxConnectionsPerClient = MaxTotalConnections;
        ConnectTimeoutSeconds = Math.Clamp(ConnectTimeoutSeconds, 1, 120);
        HeaderTimeoutSeconds = Math.Clamp(HeaderTimeoutSeconds, 1, 120);
        IdleTimeoutSeconds = Math.Clamp(IdleTimeoutSeconds, 5, 3600);
        MaxConnectionMinutes = Math.Clamp(MaxConnectionMinutes, 1, 1440);
        TemporaryEnableMinutes = Math.Clamp(TemporaryEnableMinutes, 0, 10080);
        AuthFailureLimit = Math.Clamp(AuthFailureLimit, 0, 1000);
        AuthFailureWindowSeconds = Math.Clamp(AuthFailureWindowSeconds, 30, 86400);
        AuthFailureBlockSeconds = Math.Clamp(AuthFailureBlockSeconds, 30, 86400);
        UpstreamProxyType = (UpstreamProxyType ?? "http").Trim().ToLowerInvariant();
        if (UpstreamProxyType is not ("http" or "socks5"))
            UpstreamProxyType = "http";
        UpstreamProxyHost = (UpstreamProxyHost ?? string.Empty).Trim();
        UpstreamProxyUsername = (UpstreamProxyUsername ?? string.Empty).Trim();
        UpstreamProxyPasswordProtected ??= string.Empty;
        UpstreamProxyPassword ??= string.Empty;
        AllowedPorts ??= new List<int>();
        AllowedClientCidrs ??= new List<string>();
        AllowedDomains = NormalizeDomains(AllowedDomains);
        BlockedDomains = NormalizeDomains(BlockedDomains);
        AllowedClientCidrs = AllowedClientCidrs
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        AllowedPorts = AllowedPorts
            .Where(port => port is >= 1 and <= 65535)
            .Distinct()
            .OrderBy(port => port)
            .ToList();
    }

    public ProxyOptions Clone() => new()
    {
        Enabled = Enabled,
        BindAddress = BindAddress,
        HttpEnabled = HttpEnabled,
        HttpPort = HttpPort,
        Socks5Enabled = Socks5Enabled,
        Socks5Port = Socks5Port,
        AllowAnonymous = AllowAnonymous,
        AllowedClientCidrs = new List<string>(AllowedClientCidrs),
        AllowedPorts = new List<int>(AllowedPorts),
        AllowAllPorts = AllowAllPorts,
        BlockPrivateDestinations = BlockPrivateDestinations,
        AllowedDomains = new List<string>(AllowedDomains),
        BlockedDomains = new List<string>(BlockedDomains),
        AuthFailureLimit = AuthFailureLimit,
        AuthFailureWindowSeconds = AuthFailureWindowSeconds,
        AuthFailureBlockSeconds = AuthFailureBlockSeconds,
        MaxTotalConnections = MaxTotalConnections,
        MaxConnectionsPerClient = MaxConnectionsPerClient,
        ConnectTimeoutSeconds = ConnectTimeoutSeconds,
        HeaderTimeoutSeconds = HeaderTimeoutSeconds,
        IdleTimeoutSeconds = IdleTimeoutSeconds,
        MaxConnectionMinutes = MaxConnectionMinutes,
        TemporaryEnableMinutes = TemporaryEnableMinutes,
        LogRequests = LogRequests,
        UpstreamProxyEnabled = UpstreamProxyEnabled,
        UpstreamProxyType = UpstreamProxyType,
        UpstreamProxyHost = UpstreamProxyHost,
        UpstreamProxyPort = UpstreamProxyPort,
        UpstreamProxyUsername = UpstreamProxyUsername,
        UpstreamProxyPasswordProtected = UpstreamProxyPasswordProtected,
        UpstreamProxyPassword = UpstreamProxyPassword
    };

    private static List<string> NormalizeDomains(List<string>? values) =>
        (values ?? new List<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void ValidatePort(int port, string name, List<string> errors)
    {
        // 0 仅供自动化测试使用（由系统分配临时端口）。
        if (port is < 0 or > 65535)
            errors.Add($"{name} {port} 不合法。");
    }
}
