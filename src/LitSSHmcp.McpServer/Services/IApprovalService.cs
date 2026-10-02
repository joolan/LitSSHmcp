using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using Microsoft.Extensions.Logging;

namespace LitSSHmcp.McpServer.Services;

/// <summary>
/// 审批结果。刻意把「拒绝 / 超时 / 无可用界面」区分开：
/// 三者的处置建议完全不同，坍缩成一个 false 会让模型误报"用户点了拒绝"。
/// </summary>
public enum ApprovalOutcome
{
    /// <summary>人工点了"允许"。</summary>
    Approved,
    /// <summary>人工点了"拒绝"。</summary>
    Rejected,
    /// <summary>超时窗口内无人响应，已自动拒绝。</summary>
    Timeout,
    /// <summary>本机没有可用的确认界面（非 Windows / 无桌面 / 通道未配置 / 子进程启动失败）。</summary>
    Unavailable
}

public interface IApprovalService
{
    Task<ApprovalOutcome> RequestApprovalAsync(
        string serverName,
        string command,
        CommandFilterResult filterResult,
        string? filePath = null,
        string? operationLabel = null,
        CancellationToken ct = default);
}

/// <summary>审批结果 → 工具返回的 (status, error) 文案，全仓统一出口。</summary>
public static class ApprovalOutcomeText
{
    public static (string Status, string Error) Describe(ApprovalOutcome outcome, int timeoutSeconds)
        => outcome switch
        {
            ApprovalOutcome.Rejected => ("rejected",
                "敏感操作被人工拒绝执行。若确认该操作安全, 可调整 config.json 的 security.commandFilter 降低敏感级别。"),
            ApprovalOutcome.Timeout => ("approval_timeout",
                $"确认超时({(timeoutSeconds > 0 ? timeoutSeconds : 300)}秒内无人响应), 已自动拒绝。" +
                "请在超时时间内于桌面点击\"允许\", 或设置 security.approval.channels=[\"cli\"] 改用带外审批。"),
            ApprovalOutcome.Unavailable => ("approval_unavailable",
                "本机没有可用的确认界面(非Windows/无桌面/审批通道未启用)。" +
                "请在 config.json 设置 security.approval.channels=[\"cli\"], 再用 'litssh approvals' 查看、'litssh approve <id>' 批准。"),
            _ => ("failed", "审批失败")
        };
}

/// <summary>
/// 授权确认服务，三种方式（security.approval.style）：
/// - process：启动独立子进程显示自绘弹窗（推荐，规避宿主隐藏窗口问题；带超时自动拒绝）；
/// - dialog ：在当前进程内显示自绘弹窗（带超时）；
/// - native ：原生 MessageBox（最稳，带超时）。
/// 子进程/自绘失败会自动回退原生；任何显示失败都返回 <see cref="ApprovalOutcome.Unavailable"/>（fail-closed）。
/// </summary>
public class DesktopApprovalService : IApprovalService
{
    private readonly ISecurityOptionsProvider _securityOptions;
    private readonly ILogger<DesktopApprovalService>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DesktopApprovalService(ISecurityOptionsProvider securityOptions, ILogger<DesktopApprovalService>? logger = null)
    {
        _securityOptions = securityOptions;
        _logger = logger;
    }

    public async Task<ApprovalOutcome> RequestApprovalAsync(
        string serverName,
        string command,
        CommandFilterResult filterResult,
        string? filePath = null,
        string? operationLabel = null,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger?.LogWarning("非 Windows 环境，无法显示桌面授权弹窗");
            return ApprovalOutcome.Unavailable;
        }

        var options = _securityOptions.Approval;
        var operation = operationLabel ?? (filterResult == CommandFilterResult.Sensitive ? "敏感命令" : "文件传输");
        var style = string.IsNullOrWhiteSpace(options.Style) ? "dialog" : options.Style.Trim().ToLowerInvariant();

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ApprovalOutcome.Unavailable;
        }

        try
        {
            switch (style)
            {
                case "native":
                    return ShowNative(serverName, operation, command, filePath, options);
                case "process":
                    return await RequestViaChildProcessAsync(serverName, operation, command, filePath, options, ct).ConfigureAwait(false);
                default:
                    return await RequestInProcessAsync(serverName, operation, command, filePath, options, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ApprovalOutcome> RequestInProcessAsync(
        string serverName, string operation, string command, string? filePath, ApprovalConfig options, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                _logger?.LogInformation("显示自绘授权弹窗(进程内): 服务器={Server} 操作={Operation}", serverName, operation);

                using var dialog = new ApprovalDialog(serverName, operation, command, filePath, options.TimeoutSeconds, options.TopMost);
                var result = dialog.ShowDialog();
                sw.Stop();

                _logger?.LogInformation("自绘授权弹窗关闭: 结果={Result} 耗时={ElapsedMs}ms", result, sw.ElapsedMilliseconds);
                tcs.TrySetResult(result == DialogResult.OK
                    ? ApprovalOutcome.Approved
                    : dialog.TimedOut ? ApprovalOutcome.Timeout : ApprovalOutcome.Rejected);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "自绘授权弹窗失败，回退原生");
                tcs.TrySetResult(SafeNative(serverName, operation, command, filePath, options));
            }
        })
        {
            IsBackground = true,
            Name = "LitSSH-ApprovalDialog"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // 客户端取消时立刻把结果交还给调用方，不等弹窗自己关（弹窗带超时，会自行收尾）。
        await using var reg = ct.Register(() => tcs.TrySetResult(ApprovalOutcome.Unavailable));
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task<ApprovalOutcome> RequestViaChildProcessAsync(
        string serverName, string operation, string command, string? filePath, ApprovalConfig options, CancellationToken ct)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            _logger?.LogWarning("无法定位可执行文件，审批子进程不可用，回退原生");
            return SafeNative(serverName, operation, command, filePath, options);
        }

        var request = new ApprovalRequest
        {
            Server = serverName,
            Operation = operation,
            Command = command,
            FilePath = filePath,
            TimeoutSeconds = options.TimeoutSeconds,
            TopMost = options.TopMost
        };

        var requestFile = Path.Combine(Path.GetTempPath(), $"litssh-approval-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(requestFile, ApprovalRequestHost.Serialize(request));

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"{ApprovalRequestHost.ArgumentName} \"{requestFile}\"",
                // 关键：子进程是控制台程序，必须抑制控制台窗口；
                // 同时不使用 UseShellExecute，避免继承宿主的隐藏窗口设置（只影响控制台分配）。
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                // 关键：必须重定向并排空子进程输出。子进程继承的 stdout 就是 MCP 的 JSON-RPC 管道,
                // 一旦以 `dotnet xxx.dll` 方式启动(Environment.ProcessPath=dotnet.exe), .NET 的 usage
                // 文本会直接写进协议流导致客户端解析全线崩溃。
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _logger?.LogInformation("启动审批子进程: {Exe}", exePath);

            using var process = Process.Start(psi);
            if (process == null)
            {
                _logger?.LogWarning("审批子进程启动失败，回退原生");
                return SafeNative(serverName, operation, command, filePath, options);
            }

            // 子进程输出必须持续排空，否则缓冲区写满会把子进程卡死。
            _ = DrainAsync(process.StandardOutput);
            _ = DrainAsync(process.StandardError);

            var waitMs = (options.TimeoutSeconds > 0 ? options.TimeoutSeconds + 20 : 300) * 1000;
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(waitMs);

            try
            {
                await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                _logger?.LogWarning(ct.IsCancellationRequested
                    ? "审批等待被客户端取消，按不可用处理"
                    : "审批子进程超时未退出，按超时处理");
                return ct.IsCancellationRequested ? ApprovalOutcome.Unavailable : ApprovalOutcome.Timeout;
            }

            _logger?.LogInformation("审批子进程退出: code={Code}", process.ExitCode);
            return process.ExitCode switch
            {
                ApprovalRequestHost.ExitAllow => ApprovalOutcome.Approved,
                ApprovalRequestHost.ExitTimeout => ApprovalOutcome.Timeout,
                _ => ApprovalOutcome.Rejected
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "审批子进程方式失败，回退原生");
            return SafeNative(serverName, operation, command, filePath, options);
        }
        finally
        {
            try { File.Delete(requestFile); } catch { /* ignore */ }
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is not null)
            {
                // 丢弃：子进程的一切 stdout 都不允许进入协议流
            }
        }
        catch
        {
            // 子进程退出后读取会抛，忽略
        }
    }

    private ApprovalOutcome SafeNative(string serverName, string operation, string command, string? filePath, ApprovalConfig options)
    {
        try
        {
            return ShowNative(serverName, operation, command, filePath, options);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "原生授权弹窗失败，按不可用处理");
            return ApprovalOutcome.Unavailable;
        }
    }

    private const uint MB_OK = 0x00000000;
    private const uint MB_OKCANCEL = 0x00000001;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_DEFBUTTON2 = 0x00000100;
    private const uint MB_SETFOREGROUND = 0x00010000;
    private const uint MB_TOPMOST = 0x00040000;
    private const int IDOK = 1;
    private const int IDCANCEL = 2;
    private const int IDTIMEOUT = 32000;

    /// <summary>
    /// user32 的 MessageBoxTimeout（带超时的 MessageBox）。原生 MessageBox 没有 MB_TIMEOUT 标志,
    /// 一旦用户不在(或窗口不可见)会永久阻塞, 并把内部信号量 gate 占死导致后续所有审批都失败。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxTimeout(
        IntPtr hWnd, string text, string caption, uint type, ushort languageId, uint milliseconds);

    private ApprovalOutcome ShowNative(string serverName, string operation, string command, string? filePath, ApprovalConfig options)
    {
        var message = $"服务器: {serverName}\n操作: {operation}\n命令: {command}";
        if (filePath != null)
            message += $"\n文件: {filePath}";
        message += "\n\n是否允许执行?";

        var flags = MB_OKCANCEL | MB_ICONWARNING | MB_DEFBUTTON2 | MB_SETFOREGROUND;
        if (options.TopMost)
            flags |= MB_TOPMOST;

        var timeoutMs = (uint)Math.Max(1, options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 300) * 1000u;

        var rc = MessageBoxTimeout(IntPtr.Zero, message, "LitSSH MCP - 操作确认", flags, 0, timeoutMs);
        return rc switch
        {
            IDOK => ApprovalOutcome.Approved,
            IDCANCEL => ApprovalOutcome.Rejected,
            IDTIMEOUT => ApprovalOutcome.Timeout,
            _ => ApprovalOutcome.Rejected
        };
    }
}
