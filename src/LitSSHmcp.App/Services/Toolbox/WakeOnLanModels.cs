namespace LitSSHmcp.App.Services.Toolbox;

public sealed class WakeOnLanHost
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string BroadcastAddress { get; set; } = string.Empty;
    public int Port { get; set; } = WakeOnLanService.DefaultPort;
    public string Notes { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastWokenAt { get; set; }
    public int WakeCount { get; set; }

    public WakeOnLanHost Clone() => (WakeOnLanHost)MemberwiseClone();
}

public sealed class WakeOnLanDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<WakeOnLanHost> Hosts { get; set; } = new();
}

/// <summary>本机可用的 IPv4 网卡与子网信息，用于计算定向广播地址和扫描范围。</summary>
public sealed class WakeOnLanAdapter
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required System.Net.IPAddress Address { get; init; }
    public required System.Net.IPAddress Mask { get; init; }
    public required System.Net.IPAddress Network { get; init; }
    public required System.Net.IPAddress Broadcast { get; init; }
    public required int PrefixLength { get; init; }

    public string Cidr => $"{Network}/{PrefixLength}";

    public string Display =>
        $"{Name} · {Address}/{PrefixLength} · 广播 {Broadcast}";

    public bool Contains(System.Net.IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        var value = WakeOnLanService.ToUInt32(address);
        var network = WakeOnLanService.ToUInt32(Network);
        var broadcast = WakeOnLanService.ToUInt32(Broadcast);
        return value >= network && value <= broadcast;
    }
}

public sealed class WakeOnLanScanEntry
{
    public required string IpAddress { get; init; }
    public required string MacAddress { get; init; }
    public string HostName { get; set; } = string.Empty;
    public bool RespondedToPing { get; set; }
    public bool AlreadySaved { get; set; }

    public string StatusText => AlreadySaved
        ? "已保存"
        : RespondedToPing
            ? "在线"
            : "ARP 缓存";
}

public sealed class WakeOnLanScanProgress
{
    public required int Probed { get; init; }
    public required int Total { get; init; }
    public bool ReadingArpTable { get; init; }
}

public sealed record WakeOnLanScanResult(
    IReadOnlyList<WakeOnLanScanEntry> Entries,
    int ProbedCount,
    string Subnet);

public sealed record WakeOnLanSendResult(
    bool Success,
    string Message,
    IReadOnlyList<string> BroadcastTargets);
