// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + get_usage_guide 内置清单(由注解反射生成, 无需手改);
//   ③ 若新增了工具类, 记得在 Program.cs 注册 WithTools<T>()。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。详见 docs/TOOLS.md 顶部"同步约定"。
using System.ComponentModel;
using System.Text.RegularExpressions;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

[McpServerToolType]
public class JavaTools
{
    private static readonly Regex Pid = new("^[0-9]+$", RegexOptions.Compiled);
    private static readonly Regex PsLine = new(@"^\s*(\d+)\s+(\S+)\s+(\S+)\s+(\S+)\s+(.*)$", RegexOptions.Compiled);

    private readonly IGuardedCommandService _runner;
    private readonly IConfigService _configService;

    public JavaTools(IGuardedCommandService runner, IConfigService configService)
    {
        _runner = runner;
        _configService = configService;
    }

    [McpServerTool(Name = "java_processes", UseStructuredContent = true, OutputSchemaType = typeof(JavaProcessListDto), ReadOnly = true, OpenWorld = true)]
    [Description("列出服务器上的Java进程(pid/运行时长/CPU/内存/启动命令)。可传 appId 只看某应用的进程; 要定位应用pid后做线程/堆分析时先用它")]
    public async Task<JavaProcessListDto> JavaProcesses(
        [Description("服务器标识: ID/名称/主机名, 可用ssh_list_servers列出")] string serverId,
        [Description("可选: 应用标识(ID/名称), 只显示匹配该应用的Java进程")] string? appId = null,
        CancellationToken cancellationToken = default)
    {
        var command = "ps -eo pid,etime,%cpu,%mem,args --no-headers | grep '[j]ava'";

        if (!string.IsNullOrWhiteSpace(appId))
        {
            var (tokens, status, error) = await MatchTokensAsync(appId);
            if (tokens == null)
                return JavaProcessListDto.Fail(status!, error!);
            command += $" | grep -i -E -- {ShellQuote.Single(Pattern(tokens))}";
        }

        command += " | head -50";

        var outcome = await _runner.RunAsync(serverId, command, cancellationToken);
        if (!outcome.Success)
            return JavaProcessListDto.Fail(outcome.Status ?? "failed", outcome.Error ?? "查询 Java 进程失败。");

        var processes = new List<JavaProcessDto>();
        foreach (var line in outcome.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = PsLine.Match(line);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out var pid))
                continue;

            processes.Add(new JavaProcessDto
            {
                Pid = pid,
                Elapsed = m.Groups[2].Value,
                Cpu = m.Groups[3].Value,
                Mem = m.Groups[4].Value,
                Command = m.Groups[5].Value.Trim()
            });
        }

        return new JavaProcessListDto
        {
            Success = true,
            ServerId = outcome.ServerId,
            ServerName = outcome.ServerName,
            Host = outcome.ServerHost,
            Count = processes.Count,
            Processes = processes
        };
    }

    [McpServerTool(Name = "java_threads", UseStructuredContent = true, OutputSchemaType = typeof(RemoteCommandResultDto), ReadOnly = true, OpenWorld = true)]
    [Description("抓取Java进程线程栈(jstack -l)。传 pid, 或传 appId 自动解析该应用的pid(唯一命中才行)。分析死锁/CPU飙高/线程池耗尽/阻塞时用")]
    public async Task<RemoteCommandResultDto> JavaThreads(
        [Description("服务器标识: ID/名称/主机名")] string serverId,
        [Description("Java进程 pid(用java_processes获取); 与 appId 二选一")] string? pid = null,
        [Description("应用标识(ID/名称): 自动解析其Java进程pid; 与 pid 二选一")] string? appId = null,
        CancellationToken cancellationToken = default)
    {
        var (resolved, status, error) = await ResolvePidAsync(serverId, pid, appId, cancellationToken);
        if (resolved == null)
            return new RemoteCommandResultDto { Success = false, Status = status, Error = error };

        var command = $"jstack -l {resolved}";
        return RemoteCommandResultDto.From(await _runner.RunAsync(serverId, command, cancellationToken));
    }

    [McpServerTool(Name = "java_heap", UseStructuredContent = true, OutputSchemaType = typeof(RemoteCommandResultDto), ReadOnly = true, OpenWorld = true)]
    [Description("查看Java堆内存与GC情况(jcmd GC.heap_info + jstat -gcutil)。传 pid, 或传 appId 自动解析pid。内存涨/频繁GC/OOM排查用")]
    public async Task<RemoteCommandResultDto> JavaHeap(
        [Description("服务器标识: ID/名称/主机名")] string serverId,
        [Description("Java进程 pid; 与 appId 二选一")] string? pid = null,
        [Description("应用标识(ID/名称): 自动解析其Java进程pid; 与 pid 二选一")] string? appId = null,
        CancellationToken cancellationToken = default)
    {
        var (resolved, status, error) = await ResolvePidAsync(serverId, pid, appId, cancellationToken);
        if (resolved == null)
            return new RemoteCommandResultDto { Success = false, Status = status, Error = error };

        var command = $"jcmd {resolved} GC.heap_info; echo '--- jstat -gcutil ---'; jstat -gcutil {resolved} 1000 1";
        return RemoteCommandResultDto.From(await _runner.RunAsync(serverId, command, cancellationToken));
    }

    [McpServerTool(Name = "java_info", UseStructuredContent = true, OutputSchemaType = typeof(RemoteCommandResultDto), ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("查看Java进程的JVM版本与运行时长(jcmd VM.version / VM.uptime)。传 pid, 或传 appId 自动解析pid。确认JDK版本与是否刚重启过用")]
    public async Task<RemoteCommandResultDto> JavaInfo(
        [Description("服务器标识: ID/名称/主机名")] string serverId,
        [Description("Java进程 pid; 与 appId 二选一")] string? pid = null,
        [Description("应用标识(ID/名称): 自动解析其Java进程pid; 与 pid 二选一")] string? appId = null,
        CancellationToken cancellationToken = default)
    {
        var (resolved, status, error) = await ResolvePidAsync(serverId, pid, appId, cancellationToken);
        if (resolved == null)
            return new RemoteCommandResultDto { Success = false, Status = status, Error = error };

        var command = $"jcmd {resolved} VM.version; echo '--- uptime ---'; jcmd {resolved} VM.uptime";
        return RemoteCommandResultDto.From(await _runner.RunAsync(serverId, command, cancellationToken));
    }

    /// <summary>pid 优先；否则按 appId 解析：在目标机的 Java 进程里匹配应用的名称/容器名，唯一命中才返回。</summary>
    private async Task<(string? Pid, string? Status, string? Error)> ResolvePidAsync(
        string serverId, string? pid, string? appId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(pid))
            return Pid.IsMatch(pid) ? (pid, null, null) : (null, "invalid_argument", $"非法的 pid: {pid}");

        if (string.IsNullOrWhiteSpace(appId))
            return (null, "pid_required", "必须提供 pid(用 java_processes 获取) 或 appId(自动解析该应用的 Java 进程) 之一。");

        var (tokens, tokenStatus, tokenError) = await MatchTokensAsync(appId);
        if (tokens == null)
            return (null, tokenStatus, tokenError);

        var command = $"ps -eo pid,args --no-headers | grep '[j]ava' | grep -i -E -- {ShellQuote.Single(Pattern(tokens))} | head -20";
        var outcome = await _runner.RunAsync(serverId, command, ct);
        if (!outcome.Success && outcome.ExitCode != 1)
            return (null, outcome.Status ?? "failed", outcome.Error ?? "解析 Java 进程失败。");

        var matches = outcome.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        if (matches.Length == 0)
            return (null, "pid_not_found",
                $"未找到应用 '{appId}' 对应的 Java 进程(匹配: {string.Join(", ", tokens)})。可先用 java_processes 查看实际进程与启动参数。");

        if (matches.Length > 1)
            return (null, "pid_ambiguous",
                $"应用 '{appId}' 匹配到多个 Java 进程, 请用 pid 明确指定: " +
                string.Join("; ", matches.Take(5).Select(m => $"[{m.Split(' ', 2)[0]}] {(m.Length > 120 ? m[..120] : m)}")));

        var first = matches[0];
        var space = first.IndexOf(' ');
        return (space > 0 ? first[..space] : first, null, null);
    }

    /// <summary>取应用的匹配关键词：应用名 + 容器名（非空去重）。</summary>
    private async Task<(string[]? Tokens, string? Status, string? Error)> MatchTokensAsync(string appId)
    {
        var config = await _configService.LoadConfigAsync();
        var (app, status, error) = ToolSupport.ResolveApplication(config, appId);
        if (app == null)
            return (null, status, error);

        var tokens = new[] { app.Name, app.ContainerName }
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return tokens.Length == 0
            ? (null, "app_no_match_tokens", $"应用 {app.Id} 没有可用于匹配进程的名称/容器名。")
            : (tokens, null, null);
    }

    private static string Pattern(string[] tokens) =>
        string.Join("|", tokens.Select(Regex.Escape));
}
