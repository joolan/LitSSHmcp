using System.Diagnostics;
using System.IO;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 轻量性能日志。设置环境变量 <c>LITSSH_PERF=1</c> 后，
/// 会把耗时写入 <c>%APPDATA%\LitSSH\topology-perf.log</c>（未启用时零开销）。
/// </summary>
public static class PerfLog
{
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("LITSSH_PERF") is "1" or "true";

    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(ConfigPaths.AppDataDir, "topology-perf.log");

    public static void Write(string message)
    {
        if (!Enabled)
            return;

        try
        {
            lock (Gate)
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响界面
        }
    }

    /// <summary>计时作用域（Dispose 时写日志）。未启用日志时返回空实现。</summary>
    public static IDisposable Scope(string name, Func<string>? detail = null) =>
        Enabled ? new Timer(name, detail) : NoopScope.Instance;

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }

    private sealed class Timer : IDisposable
    {
        private readonly string _name;
        private readonly Func<string>? _detail;
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        public Timer(string name, Func<string>? detail)
        {
            _name = name;
            _detail = detail;
        }

        public void Dispose()
        {
            _sw.Stop();
            Write($"{_name}: {_sw.Elapsed.TotalMilliseconds:F2} ms {_detail?.Invoke()}".TrimEnd());
        }
    }
}
