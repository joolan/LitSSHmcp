using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using Microsoft.Extensions.Logging;

namespace LitSSHmcp.McpServer.Services;

public interface IApprovalService
{
    Task<bool> RequestApprovalAsync(string serverName, string command, CommandFilterResult filterResult, string? filePath = null, string? operationLabel = null);
}

/// <summary>
/// 授权确认服务，三种方式（security.approval.style）：
/// - process：启动独立子进程显示自绘弹窗（推荐，规避宿主隐藏窗口问题；带超时自动拒绝）；
/// - dialog ：在当前进程内显示自绘弹窗（带超时）；
/// - native ：原生置顶 MessageBox（最稳，无超时）。
/// 子进程/自绘失败会自动回退原生；任何显示失败都按拒绝处理（fail-closed）。
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

    public async Task<bool> RequestApprovalAsync(
        string serverName,
        string command,
        CommandFilterResult filterResult,
        string? filePath = null,
        string? operationLabel = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger?.LogWarning("非 Windows 环境，授权请求直接拒绝");
            return false;
        }

        var options = _securityOptions.Approval;
        var operation = operationLabel ?? (filterResult == CommandFilterResult.Sensitive ? "敏感命令" : "文件传输");
        var style = string.IsNullOrWhiteSpace(options.Style) ? "dialog" : options.Style.Trim().ToLowerInvariant();

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return style switch
            {
                "native" => ShowNative(serverName, operation, command, filePath, options.TopMost),
                "process" => await RequestViaChildProcessAsync(serverName, operation, command, filePath, options).ConfigureAwait(false),
                _ => await RequestInProcessAsync(serverName, operation, command, filePath, options).ConfigureAwait(false)
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> RequestInProcessAsync(string serverName, string operation, string command, string? filePath, ApprovalConfig options)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

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
                tcs.TrySetResult(result == DialogResult.OK);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "自绘授权弹窗失败，回退原生");
                tcs.TrySetResult(SafeNative(serverName, operation, command, filePath, options.TopMost));
            }
        })
        {
            IsBackground = true,
            Name = "LitSSH-ApprovalDialog"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return await tcs.Task.ConfigureAwait(false);
    }

    private Task<bool> RequestViaChildProcessAsync(string serverName, string operation, string command, string? filePath, ApprovalConfig options)
    {
        return Task.Run(() =>
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                return SafeNative(serverName, operation, command, filePath, options.TopMost);

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
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                _logger?.LogInformation("启动审批子进程: {Exe}", exePath);

                using var process = Process.Start(psi);
                if (process == null)
                {
                    _logger?.LogWarning("审批子进程启动失败，回退原生");
                    return SafeNative(serverName, operation, command, filePath, options.TopMost);
                }

                var waitMs = (options.TimeoutSeconds > 0 ? options.TimeoutSeconds + 20 : 300) * 1000;
                if (!process.WaitForExit(waitMs))
                {
                    _logger?.LogWarning("审批子进程超时未退出，按拒绝处理");
                    try { process.Kill(); } catch { /* ignore */ }
                    return false;
                }

                _logger?.LogInformation("审批子进程退出: code={Code}", process.ExitCode);
                return process.ExitCode == ApprovalRequestHost.ExitAllow;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "审批子进程方式失败，回退原生");
                return SafeNative(serverName, operation, command, filePath, options.TopMost);
            }
            finally
            {
                try { File.Delete(requestFile); } catch { /* ignore */ }
            }
        });
    }

    private bool SafeNative(string serverName, string operation, string command, string? filePath, bool topMost)
    {
        try
        {
            return ShowNative(serverName, operation, command, filePath, topMost);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "原生授权弹窗失败，按拒绝处理");
            return false;
        }
    }

    private const uint MB_YESNO = 0x00000004;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_DEFBUTTON2 = 0x00000100;
    private const uint MB_SETFOREGROUND = 0x00010000;
    private const uint MB_TOPMOST = 0x00040000;
    private const int IDYES = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private static bool ShowNative(string serverName, string operation, string command, string? filePath, bool topMost)
    {
        var message = $"服务器: {serverName}\n操作: {operation}\n命令: {command}";
        if (filePath != null)
            message += $"\n文件: {filePath}";
        message += "\n\n是否允许执行?";

        var flags = MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2 | MB_SETFOREGROUND;
        if (topMost)
            flags |= MB_TOPMOST;

        return MessageBox(IntPtr.Zero, message, "LitSSH MCP - 操作确认", flags) == IDYES;
    }
}
