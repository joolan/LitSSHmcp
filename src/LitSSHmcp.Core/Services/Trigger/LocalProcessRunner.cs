using System.Diagnostics;
using System.Text;

namespace LitSSHmcp.Core.Services.Trigger;

public sealed class LocalProcessResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// 本机命令执行器（触发事件的本地 git / tar 用）：捕获 stdout/stderr、超时 kill、支持取消。
/// 用 <paramref name="arguments"/> 列表而非拼接字符串，由 ArgumentList 负责引号转义。
/// </summary>
public static class LocalProcessRunner
{
    public static async Task<LocalProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
        int timeoutSeconds = 120, CancellationToken ct = default, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var sw = Stopwatch.StartNew();
        var result = new LocalProcessResult();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);
            if (!string.IsNullOrEmpty(workingDirectory))
                psi.WorkingDirectory = workingDirectory;
            if (environment is not null)
            {
                foreach (var kv in environment)
                    psi.Environment[kv.Key] = kv.Value;
            }

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                result.Error = "进程启动失败";
                return result;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                result.Error = ct.IsCancellationRequested ? "已取消" : "执行超时";
                // 收割管道读取任务，避免未观察异常；保留已读到的部分输出便于排障
                try { result.Output = (await stdoutTask).Trim(); } catch { /* 管道已断开 */ }
                try { result.Error = (await stderrTask).Trim(); if (string.IsNullOrEmpty(result.Error)) result.Error = ct.IsCancellationRequested ? "已取消" : "执行超时"; } catch { /* 管道已断开 */ }
                result.Duration = sw.Elapsed;
                return result;
            }

            result.ExitCode = process.ExitCode;
            result.Output = (await stdoutTask).Trim();
            result.Error = (await stderrTask).Trim();
            result.Success = process.ExitCode == 0;
            if (!result.Success && string.IsNullOrEmpty(result.Error))
                result.Error = $"退出码 {process.ExitCode}";
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            result.Success = false;
        }

        result.Duration = sw.Elapsed;
        return result;
    }

    /// <summary>
    /// 把进程 stdout 流式写入 <paramref name="output"/>（用于大体积 dump，避免整块读进内存），
    /// stderr 前缀收集（上限 64KB，避免错误输出无界增长）。支持超时 kill 与取消。
    /// 返回结果不含 <c>Output</c>（已落盘/落流），仅 Error/ExitCode/Duration。
    /// </summary>
    public static async Task<LocalProcessResult> RunToStreamAsync(string fileName, IReadOnlyList<string> arguments,
        Stream output, int timeoutSeconds = 120, CancellationToken ct = default, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var sw = Stopwatch.StartNew();
        var result = new LocalProcessResult();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);
            if (!string.IsNullOrEmpty(workingDirectory))
                psi.WorkingDirectory = workingDirectory;
            if (environment is not null)
            {
                foreach (var kv in environment)
                    psi.Environment[kv.Key] = kv.Value;
            }

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                result.Error = "进程启动失败";
                return result;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

            var stderrTask = ReadBoundedAsync(process.StandardError, 64 * 1024);
            var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
                await copyTask;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                result.Error = ct.IsCancellationRequested ? "已取消" : "执行超时";
                try { await copyTask; } catch { /* 管道已断开 */ }
            }

            result.ExitCode = process.HasExited ? process.ExitCode : -1;
            var err = await stderrTask;
            result.Success = process.HasExited && process.ExitCode == 0 && !ct.IsCancellationRequested;
            if (!result.Success)
                result.Error = string.IsNullOrWhiteSpace(err) ? (result.Error.Length > 0 ? result.Error : $"退出码 {result.ExitCode}") : err;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            result.Success = false;
        }

        result.Duration = sw.Elapsed;
        return result;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxChars)
    {
        var buffer = new char[8192];
        var sb = new StringBuilder();
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                if (sb.Length < maxChars)
                    sb.Append(buffer, 0, Math.Min(read, maxChars - sb.Length));
            }
        }
        catch
        {
            // 管道断开时返回已读部分
        }
        return sb.ToString().Trim();
    }

    /// <summary>检测本机是否存在某命令（用于 tar 等可选依赖探测）。</summary>
    public static async Task<bool> ExistsAsync(string fileName, CancellationToken ct = default)
    {
        try
        {
            var probe = await RunAsync(fileName, new[] { "--version" }, timeoutSeconds: 10, ct: ct);
            // 命令存在但 --version 非 0 也算存在（如某些 busybox 风格实现）
            return !probe.Error.Contains("找不到", StringComparison.Ordinal) &&
                   !probe.Error.Contains("系统找不到", StringComparison.Ordinal) &&
                   !probe.Error.Contains("cannot find", StringComparison.OrdinalIgnoreCase) &&
                   !probe.Error.Contains("No such file", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
