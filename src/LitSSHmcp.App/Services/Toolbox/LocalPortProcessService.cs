using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.Services.Toolbox;

public sealed class PortProcessEntry
{
    public required string Protocol { get; init; }
    public required string LocalAddress { get; init; }
    public required int LocalPort { get; init; }
    public required string RemoteAddress { get; init; }
    public required int RemotePort { get; init; }
    public required string State { get; init; }
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required string ProcessPath { get; init; }
    public bool IsListening { get; init; }

    public string LocalEndpoint => FormatEndpoint(LocalAddress, LocalPort);
    public string RemoteEndpoint => FormatEndpoint(RemoteAddress, RemotePort);
    public string ProcessDisplay => ProcessId > 0 ? $"{ProcessName} ({ProcessId})" : ProcessName;

    public string ToCopyText() =>
        $"{Protocol}\t{LocalEndpoint}\t{RemoteEndpoint}\t{State}\t{ProcessName}\t{ProcessId}\t{ProcessPath}";

    private static string FormatEndpoint(string address, int port)
        => port <= 0 ? address : $"{address}:{port}";
}

/// <summary>
/// Windows 本地端口/进程检查：读取 netstat -ano，解析 TCP/UDP 端点并映射 PID 到进程。
/// 不修改网络配置；结束进程由 UI 明确确认后执行。
/// </summary>
public sealed class LocalPortProcessService
{
    private static readonly Regex RowPattern = new(
        @"^\s*(TCP|UDP)\s+(\S+)\s+(\S+)(?:\s+(\S+))?\s+(\d+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<IReadOnlyList<PortProcessEntry>> GetEntriesAsync(CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo("netstat.exe", "-ano")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = NativeShell.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 netstat.exe。");
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"netstat 退出码 {process.ExitCode}");
            return Parse(output);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }
    }

    public static IReadOnlyList<PortProcessEntry> Parse(string output)
    {
        var processCache = new Dictionary<int, (string Name, string Path)>();
        var result = new List<PortProcessEntry>();

        foreach (var line in (output ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = RowPattern.Match(line);
            if (!match.Success)
                continue;

            var protocol = match.Groups[1].Value.ToUpperInvariant();
            var (localAddress, localPort) = ParseEndpoint(match.Groups[2].Value);
            var (remoteAddress, remotePort) = ParseEndpoint(match.Groups[3].Value);
            var state = match.Groups[4].Success ? match.Groups[4].Value : "UDP";
            if (!int.TryParse(match.Groups[5].Value, out var pid))
                continue;

            var (name, path) = ResolveProcess(pid, processCache);
            var isListening = protocol == "UDP"
                || state.Contains("LISTEN", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state, "LISTENING", StringComparison.OrdinalIgnoreCase);

            result.Add(new PortProcessEntry
            {
                Protocol = protocol,
                LocalAddress = localAddress,
                LocalPort = localPort,
                RemoteAddress = remoteAddress,
                RemotePort = remotePort,
                State = state,
                ProcessId = pid,
                ProcessName = name,
                ProcessPath = path,
                IsListening = isListening
            });
        }

        return result
            .OrderBy(item => item.LocalPort)
            .ThenBy(item => item.Protocol, StringComparer.Ordinal)
            .ThenBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static (string Address, int Port) ParseEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint is "*:*")
            return ("*", 0);

        if (endpoint[0] == '[')
        {
            var close = endpoint.LastIndexOf(']');
            if (close > 0 && close + 1 < endpoint.Length && endpoint[close + 1] == ':'
                && int.TryParse(endpoint[(close + 2)..], out var port))
                return (NormalizeAddress(endpoint[1..close]), port);
        }

        var separator = endpoint.LastIndexOf(':');
        if (separator <= 0)
            return (endpoint, 0);

        return int.TryParse(endpoint[(separator + 1)..], out var parsedPort)
            ? (NormalizeAddress(endpoint[..separator]), parsedPort)
            : (endpoint, 0);
    }

    private static string NormalizeAddress(string address)
    {
        if (IPAddress.TryParse(address, out var ip) && ip.IsIPv4MappedToIPv6)
            return ip.MapToIPv4().ToString();
        return address;
    }

    private static (string Name, string Path) ResolveProcess(
        int pid,
        Dictionary<int, (string Name, string Path)> cache)
    {
        if (pid <= 0)
            return ("System", string.Empty);
        if (cache.TryGetValue(pid, out var cached))
            return cached;

        string name;
        string path;
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
            try { path = process.MainModule?.FileName ?? string.Empty; }
            catch { path = string.Empty; }
        }
        catch
        {
            name = "无法访问";
            path = string.Empty;
        }

        var result = (name, path);
        cache[pid] = result;
        return result;
    }
}
