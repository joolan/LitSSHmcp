using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LitSSHmcp.App.Services;

public sealed record LocalDiskVolume(
    string Name,
    string Label,
    string Format,
    long TotalBytes,
    long FreeBytes,
    bool IsSystem)
{
    public double UsedPercent => TotalBytes <= 0 ? 0 : (TotalBytes - FreeBytes) * 100.0 / TotalBytes;
}

public sealed record LocalProcessSample(
    int Pid,
    string Name,
    double CpuPercent,
    long WorkingSetBytes);

public sealed record LocalSystemSnapshot(
    DateTime Timestamp,
    double CpuPercent,
    long MemoryTotalBytes,
    long MemoryAvailableBytes,
    long MemoryCommittedBytes,
    double NetworkInBytesPerSecond,
    double NetworkOutBytesPerSecond,
    TimeSpan Uptime,
    string MachineName,
    string OsName,
    string CpuName,
    int CoreCount,
    int ProcessCount,
    IReadOnlyList<LocalDiskVolume> Disks,
    IReadOnlyList<LocalProcessSample> TopCpuProcesses,
    IReadOnlyList<LocalProcessSample> TopMemoryProcesses)
{
    public long MemoryUsedBytes => Math.Max(0, MemoryTotalBytes - MemoryAvailableBytes);

    public double MemoryPercent => MemoryTotalBytes <= 0
        ? 0
        : MemoryUsedBytes * 100.0 / MemoryTotalBytes;

    public LocalDiskVolume? SystemDisk => Disks.FirstOrDefault(d => d.IsSystem) ?? Disks.FirstOrDefault();
}

/// <summary>
/// 本机（运行 LitSSH 的 Windows 电脑）指标采集。仅采集本地数据，不联网、不写入配置。
/// CPU / 内存 / 网络按采样间隔计算增量；进程 TOP 与磁盘容量做节流缓存，避免频繁枚举。
/// </summary>
public sealed class LocalSystemMetricsService : IDisposable
{
    private const int ProcessSampleSeconds = 5;
    private const int DiskSampleSeconds = 30;

    private readonly object _gate = new();

    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _hasCpuSample;

    private long _prevNetworkIn;
    private long _prevNetworkOut;
    private DateTime _prevNetworkAt;
    private bool _hasNetworkSample;

    private IReadOnlyList<LocalDiskVolume> _disks = Array.Empty<LocalDiskVolume>();
    private DateTime _lastDiskSample = DateTime.MinValue;

    private IReadOnlyList<LocalProcessSample> _topCpu = Array.Empty<LocalProcessSample>();
    private IReadOnlyList<LocalProcessSample> _topMemory = Array.Empty<LocalProcessSample>();
    private Dictionary<int, double> _prevProcessCpuMs = new();
    private DateTime _lastProcessSample = DateTime.MinValue;
    private int _processCount;

    private readonly string _machineName = Environment.MachineName;
    private readonly string _osName = ReadOsName();
    private readonly string _cpuName = ReadCpuName();
    private readonly int _coreCount = Math.Max(1, Environment.ProcessorCount);

    public LocalSystemSnapshot Sample()
    {
        lock (_gate)
        {
            var now = DateTime.Now;

            var cpu = ReadCpuPercent();
            ReadMemory(out var memoryTotal, out var memoryAvailable, out var memoryCommitted);
            var (networkIn, networkOut) = ReadNetworkRates(now);
            EnsureDiskSample(now);
            EnsureProcessSample(now);

            return new LocalSystemSnapshot(
                now,
                cpu,
                memoryTotal,
                memoryAvailable,
                memoryCommitted,
                networkIn,
                networkOut,
                TimeSpan.FromMilliseconds(Environment.TickCount64),
                _machineName,
                _osName,
                _cpuName,
                _coreCount,
                _processCount,
                _disks,
                _topCpu,
                _topMemory);
        }
    }

    private double ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return 0;

        var idleNow = idle.ToUInt64();
        var kernelNow = kernel.ToUInt64();
        var userNow = user.ToUInt64();

        if (!_hasCpuSample)
        {
            _prevIdle = idleNow;
            _prevKernel = kernelNow;
            _prevUser = userNow;
            _hasCpuSample = true;
            return 0;
        }

        var idleDelta = idleNow >= _prevIdle ? idleNow - _prevIdle : 0;
        var kernelDelta = kernelNow >= _prevKernel ? kernelNow - _prevKernel : 0;
        var userDelta = userNow >= _prevUser ? userNow - _prevUser : 0;

        _prevIdle = idleNow;
        _prevKernel = kernelNow;
        _prevUser = userNow;

        var total = kernelDelta + userDelta;
        if (total == 0)
            return 0;

        var busy = total > idleDelta ? total - idleDelta : 0;
        return Math.Clamp(busy * 100.0 / total, 0, 100);
    }

    private static void ReadMemory(out long total, out long available, out long committed)
    {
        total = 0;
        available = 0;
        committed = 0;
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status))
                return;

            total = (long)Math.Min(status.TotalPhys, (ulong)long.MaxValue);
            available = (long)Math.Min(status.AvailPhys, (ulong)long.MaxValue);
            var committedBytes = status.TotalPageFile > status.AvailPageFile
                ? status.TotalPageFile - status.AvailPageFile
                : 0;
            committed = (long)Math.Min(committedBytes, (ulong)long.MaxValue);
        }
        catch
        {
            // 指标采集失败时保持 0，由界面显示占位值
        }
    }

    private (double In, double Out) ReadNetworkRates(DateTime now)
    {
        long totalIn = 0;
        long totalOut = 0;
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var stats = adapter.GetIPStatistics();
                totalIn += stats.BytesReceived;
                totalOut += stats.BytesSent;
            }
        }
        catch
        {
            return (0, 0);
        }

        if (!_hasNetworkSample)
        {
            _prevNetworkIn = totalIn;
            _prevNetworkOut = totalOut;
            _prevNetworkAt = now;
            _hasNetworkSample = true;
            return (0, 0);
        }

        var seconds = Math.Max(0.001, (now - _prevNetworkAt).TotalSeconds);
        var inDelta = Math.Max(0, totalIn - _prevNetworkIn);
        var outDelta = Math.Max(0, totalOut - _prevNetworkOut);
        _prevNetworkIn = totalIn;
        _prevNetworkOut = totalOut;
        _prevNetworkAt = now;
        return (inDelta / seconds, outDelta / seconds);
    }

    private void EnsureDiskSample(DateTime now)
    {
        if (_lastDiskSample != DateTime.MinValue &&
            (now - _lastDiskSample).TotalSeconds < DiskSampleSeconds)
            return;

        _lastDiskSample = now;
        try
        {
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var list = new List<LocalDiskVolume>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                        continue;
                    if (drive.TotalSize <= 0)
                        continue;

                    var name = drive.Name.TrimEnd('\\');
                    var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.DriveFormat : drive.VolumeLabel;
                    list.Add(new LocalDiskVolume(
                        name,
                        string.IsNullOrWhiteSpace(label) ? "本地磁盘" : label,
                        drive.DriveFormat,
                        drive.TotalSize,
                        drive.TotalFreeSpace,
                        string.Equals(drive.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase)));
                }
                catch
                {
                    // 个别驱动器（网络盘/光驱）可能不可访问，跳过
                }
            }

            _disks = list
                .OrderByDescending(d => d.IsSystem)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            _disks = Array.Empty<LocalDiskVolume>();
        }
    }

    private void EnsureProcessSample(DateTime now)
    {
        if (_lastProcessSample != DateTime.MinValue &&
            (now - _lastProcessSample).TotalSeconds < ProcessSampleSeconds)
            return;

        var elapsedMs = _lastProcessSample == DateTime.MinValue
            ? 0
            : Math.Max(1, (now - _lastProcessSample).TotalMilliseconds);
        _lastProcessSample = now;

        var cpuValues = new Dictionary<int, double>();
        var currentCpuMs = new Dictionary<int, double>();
        var memoryValues = new List<LocalProcessSample>();
        var count = 0;

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                count++;
                var pid = process.Id;
                var name = process.ProcessName;
                long workingSet;
                double cpuMs;
                try
                {
                    workingSet = process.WorkingSet64;
                    cpuMs = process.TotalProcessorTime.TotalMilliseconds;
                }
                catch
                {
                    continue;
                }

                currentCpuMs[pid] = cpuMs;
                var cpu = 0.0;
                if (elapsedMs > 0 && _prevProcessCpuMs.TryGetValue(pid, out var prevCpuMs))
                {
                    var delta = Math.Max(0, cpuMs - prevCpuMs);
                    cpu = Math.Clamp(delta / elapsedMs / _coreCount * 100.0, 0, 100);
                }

                cpuValues[pid] = cpu;
                memoryValues.Add(new LocalProcessSample(pid, name, cpu, Math.Max(0, workingSet)));
            }
            catch
            {
                // 系统进程可能拒绝访问，跳过
            }
            finally
            {
                process.Dispose();
            }
        }

        _processCount = count;
        _prevProcessCpuMs = currentCpuMs;
        _topCpu = memoryValues
            .Where(p => p.CpuPercent > 0.01)
            .OrderByDescending(p => p.CpuPercent)
            .ThenByDescending(p => p.WorkingSetBytes)
            .Take(8)
            .ToArray();
        _topMemory = memoryValues
            .OrderByDescending(p => p.WorkingSetBytes)
            .Take(8)
            .ToArray();
    }

    private static string ReadOsName()
    {
        var version = Environment.OSVersion.Version;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string;
            var display = key?.GetValue("DisplayVersion") as string;
            if (!string.IsNullOrWhiteSpace(product))
            {
                if (version.Build >= 22000 && product.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
                    product = product.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
                return string.IsNullOrWhiteSpace(display) ? product : $"{product} · {display}";
            }
        }
        catch
        {
            // 注册表读取失败时使用默认版本文本
        }

        return version.Build >= 22000 ? "Windows 11" : "Windows 10";
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "未知 CPU";
        }
        catch
        {
            return "未知 CPU";
        }
    }


    public void Dispose()
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

}
