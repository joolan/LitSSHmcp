using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 把一次终端会话记录到 %APPDATA%\LitSSH\terminal-logs：
///  - <c>.log</c>：纯文本输出（可读）；
///  - <c>.cast</c>：asciicast v2（带时间戳的输入/输出事件，供**回放**）。
/// </summary>
public sealed class SessionLogger : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly StreamWriter? _cast;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();

    public SessionLogger(string serverName, string host, int cols = 80, int rows = 24, string? logRoot = null)
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(logRoot)
                ? Path.Combine(ConfigPaths.AppDataDir, "terminal-logs")
                : logRoot!;
            Directory.CreateDirectory(dir);
            var safe = new string(serverName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
            if (string.IsNullOrWhiteSpace(safe))
                safe = "session";
            // 同一服务器可同时开多个终端：毫秒 + 短随机后缀避免同秒/同毫秒文件名冲突。
            var stamp = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}";
            FilePath = Path.Combine(dir, $"{safe}-{stamp}.log");
            CastPath = Path.Combine(dir, $"{safe}-{stamp}.cast");

            _writer = new StreamWriter(FilePath, append: false, Encoding.UTF8) { AutoFlush = true };
            _writer.WriteLine($"=== LitSSH 会话日志 | {serverName} ({host}) | {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

            _cast = new StreamWriter(CastPath, append: false, Encoding.UTF8) { AutoFlush = true };
            _cast.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["version"] = 2,
                ["width"] = cols,
                ["height"] = rows,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["env"] = new Dictionary<string, string?> { ["SERVER"] = serverName, ["HOST"] = host }
            }));
        }
        catch
        {
            FilePath = string.Empty;
            CastPath = string.Empty;
        }
    }

    public string FilePath { get; }
    public string CastPath { get; }

    /// <summary>记录一段服务器输出（同时写 .log 与 .cast）。</summary>
    public void WriteOutput(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        text = SecretRedactor.Redact(text);
        lock (_gate)
        {
            try { _writer?.Write(text); } catch { /* ignore */ }
            AppendCast("o", text);
        }
    }

    /// <summary>记录一段键盘输入（仅 .cast；终端回显通常已包含在输出里）。</summary>
    public void WriteInput(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        lock (_gate)
            AppendCast("i", SecretRedactor.Redact(text));
    }

    /// <summary>兼容旧调用：等价于 <see cref="WriteOutput"/>。</summary>
    public void Write(string text) => WriteOutput(text);

    private void AppendCast(string type, string text)
    {
        try
        {
            var t = _clock.Elapsed.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            _cast?.WriteLine($"[{t},\"{type}\",{JsonSerializer.Serialize(text)}]");
        }
        catch
        {
            // 写盘失败忽略
        }
    }

    public void Dispose()
    {
        try { _writer?.Dispose(); } catch { /* ignore */ }
        try { _cast?.Dispose(); } catch { /* ignore */ }
    }
}
