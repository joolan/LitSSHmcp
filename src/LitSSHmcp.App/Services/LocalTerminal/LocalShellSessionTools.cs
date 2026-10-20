using System.Globalization;
using System.Text;
using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.App.Services.LocalTerminal;

public sealed class LocalShellListTool : IAgentTool
{
    public const string ToolName = "local_shell_list";

    private readonly Func<string?> _sessionKey;
    private readonly IAiTerminalBroker _broker;
    private readonly AITool _schema;

    public LocalShellListTool(Func<string?> sessionKey, IAiTerminalBroker broker)
    {
        _sessionKey = sessionKey;
        _broker = broker;
        _schema = AIFunctionFactory.Create(() => "list", ToolName, Description);
    }

    public string Name => ToolName;
    public bool Destructive => false;
    public bool ReadOnly => true;
    public string? ToolGroup => "local";
    public AITool Tool => _schema;

    private const string Description =
        "列出本 AI 会话通过 local_shell_open 打开的终端（id、名称、状态、pid、退出码、启动时间、最近输出时间、命令）。" +
        "不包含 local_shell 使用的默认一次性终端。";

    public Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        var list = _broker.List(_sessionKey() ?? "default");
        if (list.Count == 0)
            return Task.FromResult<object?>("本会话没有通过 local_shell_open 打开的终端。");

        var sb = new StringBuilder();
        sb.AppendLine("本会话终端：");
        foreach (var info in list)
        {
            sb.Append("- ").Append(info.Id).Append(" | ").Append(info.Name).Append(" | ").Append(info.Status);
            if (info.ProcessId > 0)
                sb.Append(" | pid ").Append(info.ProcessId);
            if (info.ExitCode is { } exitCode)
                sb.Append(" | 退出码 ").Append(exitCode);
            sb.Append(" | 启动 ").Append(info.StartedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            if (info.LastOutputAt is { } last)
                sb.Append(" | 最近输出 ").Append(last.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            sb.Append(" | ").Append(TrimCommand(info.Command));
            sb.AppendLine();
        }
        return Task.FromResult<object?>(sb.ToString().TrimEnd());
    }

    private static string TrimCommand(string command)
        => command.Length <= 80 ? command : command[..80] + "…";
}

public sealed class LocalShellReadTool : IAgentTool
{
    public const string ToolName = "local_shell_read";

    private readonly Func<string?> _sessionKey;
    private readonly IAiTerminalBroker _broker;
    private readonly AITool _schema;

    public LocalShellReadTool(Func<string?> sessionKey, IAiTerminalBroker broker)
    {
        _sessionKey = sessionKey;
        _broker = broker;
        _schema = AIFunctionFactory.Create(
            (string terminalId, long? cursor, int? maxChars) => (terminalId, cursor, maxChars),
            ToolName,
            Description);
    }

    public string Name => ToolName;
    public bool Destructive => false;
    public bool ReadOnly => true;
    public string? ToolGroup => "local";
    public AITool Tool => _schema;

    private const string Description =
        "读取 local_shell_open 终端的新增输出。首次读取传 cursor=0；之后把返回的 nextCursor 原样传给 cursor，避免重复。maxChars 默认 8000，最大 200000。" +
        "输出超过 maxChars 时只返回本批，继续用 nextCursor 分页即可，不会跳过中间内容。只返回该 terminalId 的输出，不会串到其他终端。";

    public Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        var terminalId = arguments is not null && arguments.TryGetValue("terminalId", out var id)
            ? id?.ToString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(terminalId))
            return Task.FromResult<object?>("错误：缺少 terminalId 参数。");

        var cursor = ParseLong(arguments is not null && arguments.TryGetValue("cursor", out var c) ? c : null, 0);
        var maxChars = (int)Math.Clamp(
            ParseLong(arguments is not null && arguments.TryGetValue("maxChars", out var m) ? m : null, 8000),
            0, 200_000);

        var result = _broker.Read(_sessionKey() ?? "default", terminalId, cursor, maxChars);
        if (result is null)
            return Task.FromResult<object?>($"未找到终端 {terminalId}。");

        var info = result.Info;
        var sb = new StringBuilder();
        sb.Append('[').Append(info.Id).Append("] ").Append(info.Name).Append(" | ").Append(info.Status)
            .Append(" | cursor ").Append(cursor).Append(" -> ").Append(result.NextCursor);
        if (result.Truncated)
            sb.Append(" | 已截断");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrEmpty(result.Text) ? "（无新输出）" : result.Text.TrimEnd());
        sb.Append("[下次读取请传 cursor=").Append(result.NextCursor).Append(']');
        return Task.FromResult<object?>(sb.ToString());
    }

    private static long ParseLong(object? value, long fallback)
    {
        if (value is null)
            return fallback;
        if (value is long l)
            return l;
        if (value is int i)
            return i;
        if (value is double d && d >= long.MinValue && d <= long.MaxValue)
            return (long)d;
        return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }
}

public sealed class LocalShellStopTool : IAgentTool
{
    public const string ToolName = "local_shell_stop";

    private readonly Func<string?> _sessionKey;
    private readonly IAiTerminalBroker _broker;
    private readonly AITool _schema;

    public LocalShellStopTool(Func<string?> sessionKey, IAiTerminalBroker broker)
    {
        _sessionKey = sessionKey;
        _broker = broker;
        _schema = AIFunctionFactory.Create((string terminalId) => terminalId, ToolName, Description);
    }

    public string Name => ToolName;
    public bool Destructive => true;
    public bool ReadOnly => false;
    public string? ToolGroup => "local";
    public AITool Tool => _schema;

    private const string Description =
        "停止 local_shell_open 打开的终端命令：先 Ctrl+C，宽限后强制结束整棵进程树，然后关闭对应终端标签。命令已经结束时也会关闭标签；停止后该 terminalId 失效。";

    public async Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        var terminalId = arguments is not null && arguments.TryGetValue("terminalId", out var id)
            ? id?.ToString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(terminalId))
            return "错误：缺少 terminalId 参数。";

        var result = await _broker.StopAsync(_sessionKey() ?? "default", terminalId, ct);
        await LocalAudit.LogAsync(ToolName, $"LOCAL_SHELL_STOP: {terminalId}",
            result.Success ? CommandStatus.Executed : CommandStatus.Failed,
            result: result.Message);
        return result.Success
            ? $"终端 {terminalId}：{result.Status}。{result.Message}"
            : result.Message;
    }
}
