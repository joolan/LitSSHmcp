using System.IO;
using System.Text;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services;

/// <summary>把一次终端会话的输出记录到 %APPDATA%\LitSSH\terminal-logs 下的日志文件（用于回放）。</summary>
public sealed class SessionLogger : IDisposable
{
    private readonly StreamWriter? _writer;

    public SessionLogger(string serverName, string host)
    {
        try
        {
            var dir = Path.Combine(ConfigPaths.AppDataDir, "terminal-logs");
            Directory.CreateDirectory(dir);
            var safe = new string(serverName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
            if (string.IsNullOrWhiteSpace(safe))
                safe = "session";
            FilePath = Path.Combine(dir, $"{safe}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _writer = new StreamWriter(FilePath, append: false, Encoding.UTF8) { AutoFlush = true };
            _writer.WriteLine($"=== LitSSH 会话日志 | {serverName} ({host}) | {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        }
        catch
        {
            FilePath = string.Empty;
        }
    }

    public string FilePath { get; }

    public void Write(string text)
    {
        try { _writer?.Write(text); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        try { _writer?.Dispose(); } catch { /* ignore */ }
    }
}
