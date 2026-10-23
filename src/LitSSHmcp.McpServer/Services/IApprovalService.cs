using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
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
    Unavailable,
    /// <summary>审批模式设为“自动拒绝”，敏感操作被直接拒绝（非人工决定）。</summary>
    AutoRejected
}

public interface IApprovalService
{
    /// <param name="riskReport">
    /// 可选的脚本风险报告（服务端规则扫描结果），会附加展示在审批界面的命令详情区；
    /// 仅影响展示，不参与执行与豁免判定。
    /// </param>
    Task<ApprovalOutcome> RequestApprovalAsync(
        string serverName,
        string command,
        CommandFilterResult filterResult,
        string? filePath = null,
        string? operationLabel = null,
        CancellationToken ct = default,
        string? riskReport = null);
}

/// <summary>审批展示文本拼接（命令 + 风险报告），桌面与带外 CLI 通道共用。</summary>
public static class ApprovalDisplay
{
    public static string Combine(string command, string? riskReport) =>
        string.IsNullOrWhiteSpace(riskReport)
            ? command
            : command + "\n\n" + riskReport;
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
            ApprovalOutcome.AutoRejected => ("rejected",
                "该敏感操作已被安全设置中的审批模式(自动拒绝)拦截。如需执行, 请在桌面 App 的「安全设置」把审批模式改回\"手动处理\"或\"自动允许授权\"。"),
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
    private readonly ApprovalRoundTracker? _roundTracker;
    private readonly ApprovalExemptionRegistry? _exemptions;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 会话标识（MCP 会话 ID），显示在审批弹窗上。
    /// 并行会话后桌面会同时出现多个弹窗，必须标明归属会话，避免审批错认（评估报告 §4.2）。
    /// </summary>
    public string? SessionLabel { get; set; }

    /// <summary>
    /// 审批界面**可见**时触发：供分发器把超时钟重置为完整窗口
    /// （本服务内部一次只显示一个弹窗，排队中的请求若沿用发起时刻会误判超时）。
    /// </summary>
    public event Action? DialogShown;

    public DesktopApprovalService(
        ISecurityOptionsProvider securityOptions,
        ILogger<DesktopApprovalService>? logger = null,
        ApprovalRoundTracker? roundTracker = null,
        ApprovalExemptionRegistry? exemptions = null)
    {
        _securityOptions = securityOptions;
        _logger = logger;
        _roundTracker = roundTracker;
        _exemptions = exemptions;
    }

    public async Task<ApprovalOutcome> RequestApprovalAsync(
        string serverName,
        string command,
        CommandFilterResult filterResult,
        string? filePath = null,
        string? operationLabel = null,
        CancellationToken ct = default,
        string? riskReport = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger?.LogWarning("非 Windows 环境，无法显示桌面授权弹窗");
            return ApprovalOutcome.Unavailable;
        }

        // 风险报告：进程内弹窗分栏展示（左命令右风险）；原生/子进程通道为单栏文本，拼接后展示。
        // 均不影响执行与豁免判定。
        var displayCommand = ApprovalDisplay.Combine(command, riskReport);

        var options = _securityOptions.Approval;
        var operation = operationLabel ?? (filterResult == CommandFilterResult.Sensitive ? "敏感命令" : "文件传输");
        var style = string.IsNullOrWhiteSpace(options.Style) ? "dialog" : options.Style.Trim().ToLowerInvariant();

        // 审批发生在工具调用的异步流内，工具名在调用线程可见（AsyncLocal）；先取出供授权登记用。
        var toolName = AuditContext.CurrentTool;
        if (string.IsNullOrWhiteSpace(toolName))
            toolName = null;

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
            var (outcome, scope) = style switch
            {
                "native" => (ShowNative(serverName, operation, displayCommand, filePath, options), ApprovalScope.Once),
                "process" => await RequestViaChildProcessAsync(serverName, operation, displayCommand, filePath, options, ct).ConfigureAwait(false),
                _ => await RequestInProcessAsync(serverName, operation, command, filePath, options, ct, riskReport).ConfigureAwait(false)
            };

            if (outcome == ApprovalOutcome.Approved)
                RecordGrant(toolName, serverName, command, filePath, scope);

            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 登记用户勾选的授权范围：会话级直接登记；轮次级要求当前轮次 ID 已知
    /// （轮次信号缺失时降级为仅本次放行，fail-safe——只会多弹窗，不会误放行）。
    /// </summary>
    private void RecordGrant(string? toolName, string serverName, string command, string? filePath, ApprovalScope scope)
    {
        if (scope == ApprovalScope.Once || _exemptions is null || toolName is null)
            return;

        if (scope == ApprovalScope.Session)
        {
            if (_exemptions.GrantSession(toolName, serverName, command, filePath))
                _logger?.LogWarning("用户授权: 工具 {Tool} 对目标 {Target} 的相同操作在本次会话期间自动放行", toolName, serverName);
            return;
        }

        var roundId = _roundTracker?.CurrentRoundId;
        if (roundId is null)
        {
            _logger?.LogWarning("用户选择了本轮次授权, 但当前轮次未知(未收到 mcp_approval_round), 降级为仅本次放行 tool={Tool}", toolName);
            return;
        }

        if (_exemptions.GrantRound(toolName, serverName, command, filePath, roundId))
        {
            _logger?.LogInformation("用户授权: 工具 {Tool} 对目标 {Target} 的相同操作在本轮次(Round={Round})内自动放行",
                toolName, serverName, roundId);
        }
    }

    private async Task<(ApprovalOutcome Outcome, ApprovalScope Scope)> RequestInProcessAsync(
        string serverName, string operation, string command, string? filePath, ApprovalConfig options, CancellationToken ct, string? riskReport = null)
    {
        var tcs = new TaskCompletionSource<(ApprovalOutcome, ApprovalScope)>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                _logger?.LogInformation("显示自绘授权弹窗(进程内): 服务器={Server} 操作={Operation}", serverName, operation);

                using var dialog = new ApprovalDialog(serverName, operation, command, filePath, options.TimeoutSeconds, options.TopMost,
                    _exemptions?.SessionGrantedTools, riskReport, SessionLabel);
                // 弹窗可见后通知分发器重置超时钟（排队弹窗不误超时）
                dialog.Shown += (_, _) => DialogShown?.Invoke();
                var result = dialog.ShowDialog();
                sw.Stop();

                _logger?.LogInformation("自绘授权弹窗关闭: 结果={Result} 耗时={ElapsedMs}ms 范围={Scope}", result, sw.ElapsedMilliseconds, dialog.Scope);
                tcs.TrySetResult(result == DialogResult.OK
                    ? (ApprovalOutcome.Approved, dialog.Scope)
                    : dialog.TimedOut ? (ApprovalOutcome.Timeout, ApprovalScope.Once) : (ApprovalOutcome.Rejected, ApprovalScope.Once));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "自绘授权弹窗失败，回退原生");
                tcs.TrySetResult((SafeNative(serverName, operation, ApprovalDisplay.Combine(command, riskReport), filePath, options), ApprovalScope.Once));
            }
        })
        {
            IsBackground = true,
            Name = "LitSSH-ApprovalDialog"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // 客户端取消时立刻把结果交还给调用方，不等弹窗自己关（弹窗带超时，会自行收尾）。
        await using var reg = ct.Register(() => tcs.TrySetResult((ApprovalOutcome.Unavailable, ApprovalScope.Once)));
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task<(ApprovalOutcome Outcome, ApprovalScope Scope)> RequestViaChildProcessAsync(
        string serverName, string operation, string command, string? filePath, ApprovalConfig options, CancellationToken ct)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            _logger?.LogWarning("无法定位可执行文件，审批子进程不可用，回退原生");
            return (SafeNative(serverName, operation, command, filePath, options), ApprovalScope.Once);
        }

        var request = new ApprovalRequest
        {
            Server = serverName,
            Operation = operation,
            Command = command,
            FilePath = filePath,
            TimeoutSeconds = options.TimeoutSeconds,
            TopMost = options.TopMost,
            SessionGrantedTools = _exemptions?.SessionGrantedTools.ToList(),
            SessionLabel = SessionLabel
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
                return (SafeNative(serverName, operation, command, filePath, options), ApprovalScope.Once);
            }

            // 子进程启动即视为弹窗可见：分发器据此重置超时钟
            DialogShown?.Invoke();

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
                return ct.IsCancellationRequested
                    ? (ApprovalOutcome.Unavailable, ApprovalScope.Once)
                    : (ApprovalOutcome.Timeout, ApprovalScope.Once);
            }

            _logger?.LogInformation("审批子进程退出: code={Code}", process.ExitCode);
            return process.ExitCode switch
            {
                ApprovalRequestHost.ExitAllow => (ApprovalOutcome.Approved, ApprovalScope.Once),
                ApprovalRequestHost.ExitAllowRound => (ApprovalOutcome.Approved, ApprovalScope.Round),
                ApprovalRequestHost.ExitAllowSession => (ApprovalOutcome.Approved, ApprovalScope.Session),
                ApprovalRequestHost.ExitTimeout => (ApprovalOutcome.Timeout, ApprovalScope.Once),
                _ => (ApprovalOutcome.Rejected, ApprovalScope.Once)
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "审批子进程方式失败，回退原生");
            return (SafeNative(serverName, operation, command, filePath, options), ApprovalScope.Once);
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
        if (!string.IsNullOrWhiteSpace(SessionLabel))
            message += $"\n会话: {SessionLabel}";
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
