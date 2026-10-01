using Microsoft.Extensions.Logging;

namespace LitSSHmcp.McpServer.Services;

/// <summary>
/// 将 MCP 服务器日志写入本机文件（按天滚动 + 保留天数），避免日志只留在 stderr 被客户端吞掉。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly int _retentionDays;
    private readonly object _gate = new();

    private StreamWriter? _writer;
    private DateTime _writerDate = DateTime.MinValue;

    public FileLoggerProvider(string dir, int retentionDays = 7)
    {
        _dir = dir;
        _retentionDays = retentionDays;
        Directory.CreateDirectory(_dir);
        PurgeOldFiles();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            if (_writer == null || _writerDate != now.Date)
            {
                _writer?.Dispose();
                var path = Path.Combine(_dir, $"mcp-{now:yyyyMMdd}.log");
                _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = true
                };
                _writerDate = now.Date;
                PurgeOldFiles();
            }

            var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{LevelText(level)}] {category}: {message}";
            if (exception != null)
                line += Environment.NewLine + exception;

            try
            {
                _writer.WriteLine(line);
            }
            catch
            {
                // 日志写入失败不得影响主流程
            }
        }
    }

    private void PurgeOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-_retentionDays);
            foreach (var file in Directory.EnumerateFiles(_dir, "mcp-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch
        {
            // 清理失败忽略
        }
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???"
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

internal sealed class FileLogger : ILogger
{
    private readonly FileLoggerProvider _provider;
    private readonly string _category;

    public FileLogger(FileLoggerProvider provider, string category)
    {
        _provider = provider;
        _category = category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        _provider.Write(logLevel, _category, formatter(state, exception), exception);
    }
}
