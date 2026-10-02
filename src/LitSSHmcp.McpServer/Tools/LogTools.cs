// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + get_usage_guide 内置清单(由注解反射生成, 无需手改);
//   ③ 若新增了工具类, 记得在 Program.cs 注册 WithTools<T>()。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。详见 docs/TOOLS.md 顶部"同步约定"。
using System.ComponentModel;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

[McpServerToolType]
public class LogTools
{
    private readonly IConfigService _configService;
    private readonly IGuardedCommandService _runner;

    public LogTools(IConfigService configService, IGuardedCommandService runner)
    {
        _configService = configService;
        _runner = runner;
    }

    [McpServerTool(Name = "log_tail", UseStructuredContent = true, OutputSchemaType = typeof(LogResultDto), ReadOnly = true, OpenWorld = true)]
    [Description("查看服务器上日志文件的尾部(按行)。可传 path(绝对路径) 或 appId(用应用管理里配置的日志路径); 路径须在白名单内。不确定路径时先用 log_find; 按关键字过滤用 log_grep; 容器日志用 docker_logs")]
    public async Task<LogResultDto> LogTail(
        [Description("服务器标识: ID/名称/主机名, 可用ssh_list_servers列出")] string serverId,
        [Description("日志文件绝对路径, 如 /var/log/myapp/app.log; 与 appId 二选一")] string? path = null,
        [Description("应用标识(ID/名称): 用其在应用管理里配置的日志路径; 与 path 二选一")] string? appId = null,
        [Description("返回最后 N 行(默认200)")] int lines = 200,
        CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadConfigAsync();
        var (resolved, status, error) = ResolvePath(config, path, appId);
        if (resolved == null)
            return LogResultDto.Fail(status!, error!);

        lines = Math.Clamp(lines, 1, Math.Max(1, config.Security.Logs.MaxLines));
        var command = $"tail -n {lines} -- {ShellQuote.Single(resolved)}";
        var outcome = await _runner.RunAsync(serverId, command, cancellationToken);

        if (!outcome.Success)
            return LogResultDto.Fail(outcome.Status ?? "read_failed",
                outcome.Error ?? $"读取日志失败: {resolved}（确认文件存在且当前账号有读权限）",
                outcome.ServerId, outcome.ServerName, outcome.ServerHost);

        return ToLogResult(resolved, pattern: null, outcome);
    }

    [McpServerTool(Name = "log_grep", UseStructuredContent = true, OutputSchemaType = typeof(LogResultDto), ReadOnly = true, OpenWorld = true)]
    [Description("在服务器日志文件中按关键字/正则检索(返回匹配行的行号)。可传 path 或 appId; 路径须在白名单内。只看尾部用 log_tail, 不确定路径先用 log_find")]
    public async Task<LogResultDto> LogGrep(
        [Description("服务器标识: ID/名称/主机名")] string serverId,
        [Description("检索关键字或正则, 如 Exception / 'OutOfMemory|GC overhead'")] string pattern,
        [Description("日志文件绝对路径; 与 appId 二选一")] string? path = null,
        [Description("应用标识(ID/名称): 用其在应用管理里配置的日志路径; 与 path 二选一")] string? appId = null,
        [Description("是否忽略大小写(默认true)")] bool ignoreCase = true,
        [Description("返回最近匹配数上限(默认200, 上限2000)")] int maxMatches = 200,
        CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadConfigAsync();
        var (resolved, status, error) = ResolvePath(config, path, appId);
        if (resolved == null)
            return LogResultDto.Fail(status!, error!);

        if (string.IsNullOrWhiteSpace(pattern))
            return LogResultDto.Fail("invalid_argument", "检索关键字不能为空。");

        maxMatches = Math.Clamp(maxMatches, 1, 2000);
        var flag = ignoreCase ? "-i " : string.Empty;
        var command = $"grep -n {flag}-- {ShellQuote.Single(pattern)} {ShellQuote.Single(resolved)} | tail -n {maxMatches}";
        var outcome = await _runner.RunAsync(serverId, command, cancellationToken);

        // grep 无匹配时退出码为 1（不是错误）
        if (!outcome.Success && outcome.ExitCode == 1 && string.IsNullOrWhiteSpace(outcome.Output))
            return new LogResultDto
            {
                Success = true,
                ServerId = outcome.ServerId,
                ServerName = outcome.ServerName,
                Host = outcome.ServerHost,
                Path = resolved,
                Pattern = pattern,
                Count = 0
            };

        if (!outcome.Success)
            return LogResultDto.Fail(outcome.Status ?? "grep_failed",
                outcome.Error ?? $"检索日志失败: {resolved}（确认文件存在且当前账号有读权限）",
                outcome.ServerId, outcome.ServerName, outcome.ServerHost);

        return ToLogResult(resolved, pattern, outcome);
    }

    [McpServerTool(Name = "log_find", UseStructuredContent = true, OutputSchemaType = typeof(LogFileListDto), ReadOnly = true, OpenWorld = true)]
    [Description("发现服务器上最近被写入的日志文件(.log, 按修改时间倒序)。不确定日志路径时先用它, 再用 log_tail/log_grep 读取; 搜索范围限于 security.logs.allowedPaths")]
    public async Task<LogFileListDto> LogFind(
        [Description("服务器标识: ID/名称/主机名")] string serverId,
        [Description("可选: 限定搜索目录(须在白名单内); 留空则在 security.logs.allowedPaths 全部路径下搜索")] string? path = null,
        [Description("只列出最近 N 分钟内修改过的文件(默认1440=24小时, 上限10080)")] int minutes = 1440,
        [Description("最多返回数量(默认100, 上限500)")] int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadConfigAsync();
        var allowed = config.Security.Logs.AllowedPaths ?? Array.Empty<string>();

        string[] roots;
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!PathPolicy.IsRemotePathAllowed(path, allowed))
                return LogFileListDto.Fail("path_not_allowed",
                    $"目录不在允许范围内: {path}。允许路径: {string.Join(", ", allowed)}");
            roots = new[] { path! };
        }
        else
        {
            roots = allowed.Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
            if (roots.Length == 0)
                return LogFileListDto.Fail("no_allowed_paths", "security.logs.allowedPaths 为空, 无法发现日志。");
        }

        minutes = Math.Clamp(minutes, 1, 10080);
        maxResults = Math.Clamp(maxResults, 1, 500);

        var rootsArg = string.Join(" ", roots.Select(ShellQuote.Single));
        var command =
            $"find {rootsArg} -type f -name '*.log' -mmin -{minutes} -printf '%T@ %p\\n' 2>/dev/null " +
            $"| sort -rn | head -{maxResults}";
        var outcome = await _runner.RunAsync(serverId, command, cancellationToken);

        if (!outcome.Success)
            return LogFileListDto.Fail(outcome.Status ?? "find_failed",
                outcome.Error ?? "日志发现失败。",
                outcome.ServerId, outcome.ServerName, outcome.ServerHost);

        var files = new List<LogFileDto>();
        foreach (var line in outcome.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r');
            var space = trimmed.IndexOf(' ');
            if (space <= 0)
                continue;

            var pathPart = trimmed[(space + 1)..].Trim();
            DateTimeOffset? modified = null;
            if (double.TryParse(trimmed[..space], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var epoch))
                modified = DateTimeOffset.FromUnixTimeSeconds((long)epoch);

            if (pathPart.Length > 0)
                files.Add(new LogFileDto { Path = pathPart, ModifiedAt = modified });
        }

        return new LogFileListDto
        {
            Success = true,
            ServerId = outcome.ServerId,
            ServerName = outcome.ServerName,
            Host = outcome.ServerHost,
            Count = files.Count,
            Truncated = outcome.Truncated || files.Count >= maxResults,
            Files = files
        };
    }

    /// <summary>
    /// 解析要读取的日志路径：path 优先；否则用应用的 LogPaths。
    /// 每次都过 security.logs.allowedPaths 白名单；多个可用路径时要求调用方用 path 指定（不静默猜）。
    /// </summary>
    private static (string? Path, string? Status, string? Error) ResolvePath(AppConfig config, string? path, string? appId)
    {
        var allowed = config.Security.Logs.AllowedPaths ?? Array.Empty<string>();

        if (!string.IsNullOrWhiteSpace(path))
        {
            return PathPolicy.IsRemotePathAllowed(path, allowed)
                ? (path, null, null)
                : (null, "path_not_allowed", $"日志路径不在允许范围内: {path}。允许路径: {string.Join(", ", allowed)}（可在安全设置中调整 security.logs.allowedPaths）");
        }

        if (string.IsNullOrWhiteSpace(appId))
            return (null, "path_required", "必须提供 path(日志文件绝对路径) 或 appId(在应用管理里配置了日志路径) 之一。不确定路径时先用 log_find。");

        var (app, status, error) = ToolSupport.ResolveApplication(config, appId!);
        if (app == null)
            return (null, status, error);

        var configured = (app.LogPaths ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (configured.Length == 0)
            return (null, "log_path_not_configured", $"应用 {app.Name} 未配置日志路径(应用管理 → 日志路径)。也可直接传 path。");

        var usable = configured.Where(p => PathPolicy.IsRemotePathAllowed(p, allowed)).ToArray();
        if (usable.Length == 0)
            return (null, "path_not_allowed", $"应用 {app.Name} 配置的日志路径都不在白名单内: {string.Join(", ", configured)}。允许路径: {string.Join(", ", allowed)}");
        if (usable.Length > 1)
            return (null, "log_path_ambiguous", $"应用 {app.Name} 配置了多个可用日志路径, 请用 path 指定其中一个: {string.Join(", ", usable)}");

        return (usable[0], null, null);
    }

    private static LogResultDto ToLogResult(string path, string? pattern, GuardedCommandOutcome outcome)
    {
        var lines = outcome.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToList();

        return new LogResultDto
        {
            Success = true,
            ServerId = outcome.ServerId,
            ServerName = outcome.ServerName,
            Host = outcome.ServerHost,
            Path = path,
            Pattern = pattern,
            Count = lines.Count,
            Truncated = outcome.Truncated,
            Lines = lines
        };
    }
}
