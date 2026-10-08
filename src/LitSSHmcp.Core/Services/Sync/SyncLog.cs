using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.Sync;

/// <summary>同步日志（写入 %APPDATA%\LitSSH\logs\sync-YYYYMMDD.log；不进入审计）。</summary>
public static class SyncLog
{
    private static readonly object Gate = new();

    public static void Write(string line)
    {
        try
        {
            Directory.CreateDirectory(ConfigPaths.LogsDir);
            var file = Path.Combine(ConfigPaths.LogsDir, $"sync-{DateTime.Now:yyyyMMdd}.log");
            lock (Gate)
                File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败忽略
        }
    }
}
