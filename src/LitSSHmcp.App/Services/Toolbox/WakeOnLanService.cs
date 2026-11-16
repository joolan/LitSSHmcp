using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.Services.Toolbox;

/// <summary>
/// Wake-on-LAN：解析 MAC、构造魔术包、向定向广播发送，并可通过 ICMP + ARP 扫描同网段设备。
/// 仅发送标准 102 字节魔术包，不修改本机网络配置。
/// </summary>
public sealed class WakeOnLanService
{
    public const int DefaultPort = 9;
    public const int MaxScanHosts = 1022;

    private const int PingTimeoutMilliseconds = 700;
    private const int ScanConcurrency = 64;
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromMilliseconds(500);

    private static readonly Regex MacPattern = new(
        "^(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$|^[0-9A-Fa-f]{4}(?:\\.[0-9A-Fa-f]{4}){2}$",
        RegexOptions.Compiled);

    private static readonly Regex ArpRowPattern = new(
        @"^\s*(\d{1,3}(?:\.\d{1,3}){3})\s+([0-9A-Fa-f]{2}(?:[-:][0-9A-Fa-f]{2}){5})\s+(\S+)\s*$",
        RegexOptions.Compiled);

    public static bool TryNormalizeMac(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var compact = input.Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        if (compact.Length != 12 || !compact.All(Uri.IsHexDigit))
            return false;

        var bytes = Convert.FromHexString(compact);
        normalized = FormatMac(bytes);
        return true;
    }

    public static string NormalizeMac(string input) =>
        TryNormalizeMac(input, out var normalized)
            ? normalized
            : throw new FormatException("MAC 地址格式无效，请使用 AA:BB:CC:DD:EE:FF 或 AABB.CCDD.EEFF。");

    public static string FormatMac(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 6)
            throw new ArgumentException("MAC 地址必须为 6 字节。", nameof(bytes));
        return string.Join(":", bytes.ToArray().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }

    public static bool LooksLikeMac(string? value) => !string.IsNullOrWhiteSpace(value) && MacPattern.IsMatch(value.Trim());

    public static byte[] BuildMagicPacket(string macAddress)
    {
        var normalized = NormalizeMac(macAddress);
        var mac = Convert.FromHexString(normalized.Replace(":", string.Empty, StringComparison.Ordinal));
        var packet = new byte[6 + 16 * 6];
        for (var i = 0; i < 6; i++)
            packet[i] = 0xFF;
        for (var repeat = 0; repeat < 16; repeat++)
            mac.CopyTo(packet, 6 + repeat * 6);
        return packet;
    }

    public static IReadOnlyList<WakeOnLanAdapter> GetAdapters()
    {
        var result = new List<WakeOnLanAdapter>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            IPInterfaceProperties properties;
            try { properties = nic.GetIPProperties(); }
            catch { continue; }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    unicast.IPv4Mask is null)
                    continue;

                var address = unicast.Address;
                if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast))
                    continue;

                var maskValue = ToUInt32(unicast.IPv4Mask);
                if (maskValue == uint.MaxValue)
                    continue;

                var prefix = CountBits(maskValue);
                if (prefix < 8)
                    continue;

                var network = FromUInt32(ToUInt32(address) & maskValue);
                var broadcast = FromUInt32((ToUInt32(address) & maskValue) | ~maskValue);
                result.Add(new WakeOnLanAdapter
                {
                    Name = nic.Name,
                    Description = nic.Description,
                    Address = address,
                    Mask = unicast.IPv4Mask,
                    Network = network,
                    Broadcast = broadcast,
                    PrefixLength = prefix
                });
            }
        }

        return result
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.Address.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    public static IReadOnlyList<IPAddress> GetScanHosts(WakeOnLanAdapter adapter)
    {
        if (adapter.PrefixLength < 22)
            throw new InvalidOperationException(
                $"网段 {adapter.Cidr} 过大，为避免影响局域网只扫描 /22（最多 {MaxScanHosts} 台）以内的网段。");

        var network = ToUInt32(adapter.Network);
        var broadcast = ToUInt32(adapter.Broadcast);
        var own = ToUInt32(adapter.Address);
        var hosts = new List<IPAddress>(MaxScanHosts);
        for (var value = network + 1; value < broadcast; value++)
        {
            if (value == own)
                continue;
            hosts.Add(FromUInt32(value));
        }
        return hosts;
    }

    public async Task<WakeOnLanSendResult> WakeAsync(
        WakeOnLanHost host,
        string? broadcastOverride = null,
        int? port = null,
        int repeat = 3,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeMac(host.MacAddress, out var mac))
            return new WakeOnLanSendResult(false, "MAC 地址无效，请检查后重试。", Array.Empty<string>());

        var targets = ResolveBroadcastTargets(host, broadcastOverride);
        if (targets.Count == 0)
            return new WakeOnLanSendResult(false, "没有可用的广播地址，请手动填写广播地址。", Array.Empty<string>());

        var usePort = port ?? host.Port;
        if (usePort is < 1 or > 65535)
            return new WakeOnLanSendResult(false, "端口必须在 1-65535 之间。", Array.Empty<string>());

        var repeatCount = Math.Clamp(repeat, 1, 10);
        var packet = BuildMagicPacket(mac);
        var targetText = targets.Select(t => $"{t}:{usePort}").ToArray();

        try
        {
            using var client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            for (var round = 0; round < repeatCount; round++)
            {
                foreach (var target in targets)
                {
                    var endpoint = new IPEndPoint(target, usePort);
                    await client.SendAsync(packet.AsMemory(), endpoint, cancellationToken)
                        .ConfigureAwait(false);
                }
                if (round + 1 < repeatCount)
                    await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            }

            return new WakeOnLanSendResult(
                true,
                $"已向 {string.Join("、", targetText)} 发送 {repeatCount} 个魔术包。目标开机通常需要 3-15 秒。",
                targetText);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new WakeOnLanSendResult(false, "发送魔术包失败：" + ex.Message, targetText);
        }
    }

    public static IReadOnlyList<IPAddress> ResolveBroadcastTargets(WakeOnLanHost host, string? broadcastOverride)
    {
        var explicitAddress = string.IsNullOrWhiteSpace(broadcastOverride)
            ? host.BroadcastAddress
            : broadcastOverride;

        if (!string.IsNullOrWhiteSpace(explicitAddress) &&
            IPAddress.TryParse(explicitAddress.Trim(), out var parsed))
            return new[] { parsed };

        var adapters = GetAdapters();
        if (IPAddress.TryParse(host.IpAddress?.Trim(), out var hostAddress))
        {
            var matching = adapters
                .Where(a => a.Contains(hostAddress))
                .Select(a => a.Broadcast)
                .Distinct()
                .ToList();
            if (matching.Count > 0)
                return matching;
        }

        var broadcasts = adapters
            .Select(a => a.Broadcast)
            .Distinct()
            .ToList();
        if (broadcasts.Count == 0)
            broadcasts.Add(IPAddress.Broadcast);
        return broadcasts;
    }

    public async Task<WakeOnLanScanResult> ScanAsync(
        WakeOnLanAdapter adapter,
        IReadOnlyCollection<string>? knownMacs = null,
        IProgress<WakeOnLanScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var hosts = GetScanHosts(adapter);
        var reachable = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        var probed = 0;
        using var gate = new SemaphoreSlim(ScanConcurrency);

        var tasks = hosts.Select(async address =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(address, PingTimeoutMilliseconds)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                    reachable[address.ToString()] = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 目标离线、拒绝 ICMP 或超时都按未响应处理，继续扫描。
            }
            finally
            {
                var current = Interlocked.Increment(ref probed);
                if (current % 8 == 0 || current == hosts.Count)
                    progress?.Report(new WakeOnLanScanProgress { Probed = current, Total = hosts.Count });
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new WakeOnLanScanProgress
        {
            Probed = hosts.Count,
            Total = hosts.Count,
            ReadingArpTable = true
        });

        var arp = await ReadArpTableAsync(cancellationToken).ConfigureAwait(false);
        var known = (knownMacs ?? Array.Empty<string>())
            .Select(m => TryNormalizeMac(m, out var normalized) ? normalized : string.Empty)
            .Where(m => m.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entries = new List<WakeOnLanScanEntry>();
        foreach (var row in arp)
        {
            if (!IPAddress.TryParse(row.IpAddress, out var address) || !adapter.Contains(address))
                continue;
            if (address.Equals(adapter.Address) || address.Equals(adapter.Broadcast) || address.Equals(adapter.Network))
                continue;
            if (!TryNormalizeMac(row.MacAddress, out var mac))
                continue;
            if (mac is "FF:FF:FF:FF:FF:FF" or "00:00:00:00:00:00")
                continue;
            if (entries.Any(e => string.Equals(e.MacAddress, mac, StringComparison.OrdinalIgnoreCase)))
                continue;

            var entry = new WakeOnLanScanEntry
            {
                IpAddress = row.IpAddress,
                MacAddress = mac,
                RespondedToPing = reachable.ContainsKey(row.IpAddress),
                AlreadySaved = known.Contains(mac)
            };
            entries.Add(entry);
        }

        await ResolveHostNamesAsync(entries, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return new WakeOnLanScanResult(
            entries
                .OrderBy(e => ToUInt32(IPAddress.Parse(e.IpAddress)))
                .ToList(),
            hosts.Count,
            adapter.Cidr);
    }

    private static async Task ResolveHostNamesAsync(
        IReadOnlyList<WakeOnLanScanEntry> entries,
        CancellationToken cancellationToken)
    {
        var tasks = entries.Select(async entry =>
        {
            if (!IPAddress.TryParse(entry.IpAddress, out var address))
                return;
            try
            {
                var resolve = Dns.GetHostEntryAsync(address);
                _ = resolve.ContinueWith(
                    task => { _ = task.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                var completed = await Task.WhenAny(resolve, Task.Delay(ResolveTimeout, cancellationToken))
                    .ConfigureAwait(false);
                if (completed == resolve)
                {
                    var result = await resolve.ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(result.HostName) &&
                        !string.Equals(result.HostName, entry.IpAddress, StringComparison.OrdinalIgnoreCase))
                        entry.HostName = result.HostName;
                }
            }
            catch
            {
                // 反向解析失败不影响扫描结果。
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public static IReadOnlyList<(string IpAddress, string MacAddress, string Type)> ParseArpTable(string output)
    {
        var rows = new List<(string, string, string)>();
        foreach (var line in (output ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = ArpRowPattern.Match(line);
            if (!match.Success)
                continue;
            rows.Add((match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<(string IpAddress, string MacAddress, string Type)>> ReadArpTableAsync(
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("arp.exe", "-a")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Default
        };

        using var process = NativeShell.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 arp.exe。");
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"arp 退出码 {process.ExitCode}");
            return ParseArpTable(output);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }
    }

    internal static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
            throw new ArgumentException("仅支持 IPv4。", nameof(address));
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    internal static IPAddress FromUInt32(uint value) =>
        new(new[]
        {
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        });

    private static int CountBits(uint value)
    {
        var count = 0;
        while (value != 0)
        {
            count += (int)(value & 1);
            value >>= 1;
        }
        return count;
    }
}
