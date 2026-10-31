// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + mcp_usage_guide 内置清单(由注解反射生成, 无需手改);
//   ③ 若新增了工具类, 记得在 Program.cs 注册 WithTools<T>()。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。详见 docs/TOOLS.md 顶部"同步约定"。
using System.ComponentModel;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Topology;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

[McpServerToolType]
public class LogTools
{
    private readonly IConfigService _configService;
    private readonly IGuardedCommandService _runner;
    private readonly IUrlResolutionService? _urlResolution;

    public LogTools(IConfigService configService, IGuardedCommandService runner,
        IUrlResolutionService? urlResolution = null)
    {
        _configService = configService;
        _runner = runner;
        _urlResolution = urlResolution;
    }

    [McpServerTool(Name = "log_query", UseStructuredContent = true, OutputSchemaType = typeof(LogQueryResultDto), ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("日志文件查询：action=tail/grep/find 分别看尾部/检索/发现日志。可传 path、appId，或只传 url 自动解析应用和服务器；路径须在 security.logs.allowedPaths 内")]
    public async Task<LogQueryResultDto> LogQuery(
        [Description("服务器 ID/名称/主机名；提供 url 时可省略")] string? serverId = null,
        [Description("查询类型：tail / grep / find")] string action = "tail",
        [Description("日志文件绝对路径（tail/grep 与 appId 二选一；find 时可作搜索目录）")] string? path = null,
        [Description("应用标识(ID/名称): 用应用管理里配置的日志路径（tail/grep）")] string? appId = null,
        [Description("按访问 URL 自动解析应用和服务器；与 serverId 二选一，多个后端时需再传 appId")] string? url = null,
        [Description("grep 检索关键字或正则")] string? pattern = null,
        [Description("tail 返回最后 N 行（默认200）；grep 时忽略")] int lines = 200,
        [Description("grep 是否忽略大小写（默认true）")] bool ignoreCase = true,
        [Description("grep 返回最近匹配数上限（默认200，上限2000）")] int maxMatches = 200,
        [Description("find 只列最近 N 分钟内修改的文件（默认1440=24小时）")] int minutes = 1440,
        [Description("find 最多返回数量（默认100，上限500）")] int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        var kind = (action ?? "tail").Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(serverId))
        {
            if (_urlResolution is null)
                return LogQueryResultDto.Fail("url_resolver_unavailable", "URL 解析服务不可用");

            var resolution = await _urlResolution.ResolveAsync(url, cancellationToken);
            if (!resolution.Success)
                return LogQueryResultDto.Fail(resolution.Error ?? "url_not_found", resolution.Summary);

            var candidates = resolution.LandingApps ?? new List<UrlMatchedApp>();
            var selected = !string.IsNullOrWhiteSpace(appId)
                ? candidates.FirstOrDefault(a =>
                    string.Equals(a.Id, appId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a.Name, appId, StringComparison.OrdinalIgnoreCase))
                : candidates.Count == 1
                    ? candidates[0]
                    : null;

            if (selected is null)
            {
                var names = string.Join(", ", candidates.Select(a => $"{a.Name}({a.Id})"));
                return LogQueryResultDto.Fail("url_ambiguous",
                    $"URL 匹配到多个应用，请用 appId 指定其中一个: {names}");
            }

            var chain = resolution.RuntimeChains?.FirstOrDefault(c => c.ApplicationId == selected.Id);
            if (chain is null || string.IsNullOrWhiteSpace(chain.ServerId))
                return LogQueryResultDto.Fail("runtime_not_resolved",
                    chain?.Error ?? $"应用 {selected.Name} 未能解析到服务器运行环境");

            serverId = chain.ServerId;
            appId = selected.Id;
        }

        if (string.IsNullOrWhiteSpace(serverId))
            return LogQueryResultDto.Fail("server_required", "必须提供 serverId 或 url");

        switch (kind)
        {
            case "tail":
                return FromResult(await LogTail(serverId!, path, appId, lines, cancellationToken), kind);
            case "grep":
                return FromResult(await LogGrep(serverId!, pattern ?? string.Empty, path, appId,
                    ignoreCase, maxMatches, cancellationToken), kind);
            case "find":
                return FromResult(await LogFind(serverId!, path, minutes, maxResults, cancellationToken), kind);
            default:
                return LogQueryResultDto.Fail("invalid_argument", "action 须为 tail / grep / find");
        }
    }

    public async Task<LogResultDto> LogTail(
        [Description("服务器 ID/名称/主机名")] string serverId,
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

    public async Task<LogResultDto> LogGrep(
        [Description("服务器 ID/名称/主机名")] string serverId,
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

    public async Task<LogFileListDto> LogFind(
        [Description("服务器 ID/名称/主机名")] string serverId,
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
            return (null, "path_required", "必须提供 path(日志文件绝对路径) 或 appId(在应用管理里配置了日志路径) 之一。不确定路径时先用 log_query action=find。");

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

    private static LogQueryResultDto FromResult(LogResultDto r, string action) => new()
    {
        Success = r.Success,
        Status = r.Status,
        Error = r.Error,
        ServerId = r.ServerId,
        ServerName = r.ServerName,
        Host = r.Host,
        Action = action,
        Path = r.Path,
        Pattern = r.Pattern,
        Count = r.Count,
        Truncated = r.Truncated,
        Lines = r.Lines
    };

    private static LogQueryResultDto FromResult(LogFileListDto r, string action) => new()
    {
        Success = r.Success,
        Status = r.Status,
        Error = r.Error,
        ServerId = r.ServerId,
        ServerName = r.ServerName,
        Host = r.Host,
        Action = action,
        Count = r.Count,
        Truncated = r.Truncated,
        Files = r.Files
    };

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
