namespace LitSSHmcp.Core.Services.Storage;

/// <summary>
/// 跨进程互斥的文件锁：以 <see cref="FileShare.None"/> 独占打开锁文件，进程崩溃时由操作系统释放句柄（自愈）。
/// 用于把「读最后 hash → 计算 → 插入」这类必须跨进程串行的临界段包住（审计哈希链追加、链库初始化）。
/// </summary>
public sealed class CrossProcessFileLock : IDisposable
{
    private readonly FileStream _stream;

    private CrossProcessFileLock(FileStream stream) => _stream = stream;

    /// <summary>
    /// 独占获取锁文件；被其它进程占用时退避重试，超过 <paramref name="timeout"/> 抛 <see cref="TimeoutException"/>。
    /// </summary>
    public static async Task<CrossProcessFileLock> AcquireAsync(string path, TimeSpan timeout, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new CrossProcessFileLock(new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1));
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                // 被其它进程（或本进程另一实例）独占：随机退避后重试，避免活锁
                await Task.Delay(Random.Shared.Next(2, 15), ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new TimeoutException($"获取跨进程锁超时（{timeout.TotalSeconds:0.#} 秒）：{path}", ex);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
