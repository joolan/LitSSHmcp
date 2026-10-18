using System.Security.Cryptography;
using System.Text;

namespace LitSSHmcp.Core.Services.Security;

/// <summary>
/// 目标级写互斥（评估报告 §4.3/M2-1）：同一目标跨进程同一时刻只允许一个写类工具执行，
/// 读类工具自由并行。以 <c>FileShare.None</c> 独占锁文件实现，进程崩溃由 OS 释放句柄（自愈）。
/// 失败方向为「拒绝」（fail-closed）：写锁拿不到就拒绝写操作，绝不放行无互斥的写。
/// </summary>
public interface ITargetWriteMutex
{
    /// <summary>
    /// 在 <paramref name="waitTimeout"/> 内自旋尝试获取目标写锁。
    /// 成功返回租约（Dispose 释放锁）；超时返回 null 且 reason=write_busy。
    /// </summary>
    ValueTask<(IDisposable? Lease, string? Reason)> AcquireAsync(
        string targetKey, TimeSpan waitTimeout, CancellationToken ct = default);
}

public sealed class FileTargetWriteMutex : ITargetWriteMutex
{
    private readonly string _rootDirectory;

    public FileTargetWriteMutex(string rootDirectory)
    {
        _rootDirectory = rootDirectory;
    }

    public async ValueTask<(IDisposable? Lease, string? Reason)> AcquireAsync(
        string targetKey, TimeSpan waitTimeout, CancellationToken ct = default)
    {
        string path;
        try
        {
            var dir = Path.Combine(_rootDirectory, "write", HashKey(targetKey));
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, "write.lock");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return (null, "write_lock_unavailable"); // 拿不到锁目录宁可拒绝，不放行无互斥的写
        }

        var deadline = DateTime.UtcNow + waitTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
                return (new WriteLease(stream), null);
            }
            catch (IOException ex) when (IsLockBusy(ex))
            {
                if (DateTime.UtcNow >= deadline)
                    return (null, "write_busy");
                await Task.Delay(25, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (null, "write_lock_unavailable");
            }
        }
    }

    /// <summary>共享冲突（ERROR_SHARING_VIOLATION=32 / ERROR_LOCK_VIOLATION=33）才等待重试。</summary>
    private static bool IsLockBusy(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    private static string HashKey(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    private sealed class WriteLease : IDisposable
    {
        private FileStream? _stream;

        public WriteLease(FileStream stream) => _stream = stream;

        public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
    }
}
