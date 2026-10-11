using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.Trigger;

/// <summary>触发事件执行日志（写入 %APPDATA%\LitSSH\logs\trigger-YYYYMMDD.log；不进入审计）。</summary>
public static class TriggerLog
{
    private static readonly object Gate = new();

    public static void Write(string line)
    {
        try
        {
            Directory.CreateDirectory(ConfigPaths.LogsDir);
            var file = Path.Combine(ConfigPaths.LogsDir, $"trigger-{DateTime.Now:yyyyMMdd}.log");
            lock (Gate)
                File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败忽略
        }
    }
}
