using System.Linq;
using LitSSHmcp.Agent;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.App.Services.LocalTerminal;

/// <summary>用户在审批对话框里的选择。</summary>
public enum LocalApprovalDecision
{
    Deny,
    Allow,
    AlwaysAllow
}

/// <summary>请求人工审批（由 App 提供对话框实现）。</summary>
public delegate LocalApprovalDecision LocalApprovalHandler(string command, string workingDirectory);

/// <summary>
/// 暴露给 AI 的本机 CLI 工具：把命令送进用户可见的共享 PowerShell 终端执行。
/// 安全：硬白名单（仅白名单内的 CLI）→ 危险规则拦截 → 人工审批 → 执行并回传干净输出。
/// </summary>
public sealed class LocalTerminalTool : IAgentTool
{
    private readonly Func<string?> _sessionKey;
    private readonly Func<string, LocalTerminalSessionViewModel?> _terminalResolver;
    private readonly LocalTerminalConfig _config;
    private readonly LocalApprovalHandler _approval;
    private readonly Action _persist;
    private readonly AITool _schema;

    public LocalTerminalTool(
        Func<string?> sessionKey,
        Func<string, LocalTerminalSessionViewModel?> terminalResolver,
        LocalTerminalConfig config,
        LocalApprovalHandler approval,
        Action persist)
    {
        _sessionKey = sessionKey;
        _terminalResolver = terminalResolver;
        _config = config;
        _approval = approval;
        _persist = persist;
        _schema = AIFunctionFactory.Create(
            (string command) => command,
            Name,
            Description);
    }

    public const string ToolName = "local_shell";

    public string Name => ToolName;

    public bool Destructive => true;

    public bool ReadOnly => false;

    public string? ToolGroup => "local";

    public AITool Tool => _schema;

    private const string Description =
        "在本机 Windows PowerShell 终端中执行一条 CLI 命令（命令与输出对用户可见）。" +
        "仅允许白名单内的 CLI（如 git/dotnet/npm/gh/aws/az/terraform 等），且可能需用户人工审批。" +
        "参数 command 为完整命令行；返回命令的 stdout/stderr 与退出码。";

    public async Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        var command = arguments is not null && arguments.TryGetValue("command", out var c) ? c?.ToString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(command))
            return "错误：缺少 command 参数。";

        var policy = LocalCommandPolicy.Check(command, _config.AllowedCommands);
        if (policy == LocalPolicyResult.Blocked)
            return "已拒绝：该命令被本机安全策略判定为危险或无效，禁止执行。";
        if (policy == LocalPolicyResult.NotInAllowlist)
            return $"已拒绝：命令 '{LocalCommandPolicy.FirstExecutable(command)}' 不在本机 CLI 白名单内。" +
                   $"允许的 CLI：{string.Join(", ", _config.AllowedCommands)}。若确需，请在「AI 助手设置 → 本机终端」中扩充白名单。";

        var terminal = _terminalResolver(_sessionKey() ?? "default");
        if (terminal is null)
            return "错误：本机终端不可用（无法创建共享会话）。";

        var autoApproved = _config.AutoApprovedCommands.Any(x => string.Equals(x, command, StringComparison.Ordinal));
        if (!autoApproved)
        {
            var decision = _approval(command, terminal.Subtitle);
            if (decision == LocalApprovalDecision.Deny)
                return "用户拒绝执行该命令。";
            if (decision == LocalApprovalDecision.AlwaysAllow)
            {
                _config.AutoApprovedCommands = _config.AutoApprovedCommands
                    .Append(command).Distinct(StringComparer.Ordinal).ToArray();
                _persist();
            }
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(_config.TimeoutSeconds, 1, 1800));
        try
        {
            var result = await terminal.RunCommandAsync(command, timeout, ct);
            var output = result.Output ?? string.Empty;
            if (output.Length > _config.MaxOutputChars)
                output = output[.._config.MaxOutputChars] + "\n…[输出已截断]";
            var status = result.TimedOut
                ? $"超时 {timeout.TotalSeconds:0}s，已发送中断"
                : $"退出码 {result.ExitCode}";
            return output.Length == 0 ? $"[{status}]（无输出）" : $"[{status}]\n{output}";
        }
        catch (Exception ex)
        {
            return "执行失败：" + ex.Message;
        }
    }
}
