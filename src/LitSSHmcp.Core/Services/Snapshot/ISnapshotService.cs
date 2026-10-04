using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.Snapshot;

public interface ISnapshotService
{
    /// <summary>
    /// 同步生成一份服务器快照并落库（阻塞到完成）。同一服务器同时只允许一个快照：
    /// 并发调用返回 status=snapshot_in_progress（含进行中的快照 Id），不排队、不重复采集。
    /// </summary>
    Task<SnapshotRefreshResult> RefreshAsync(SshServerConfig server, SnapshotConfig options, CancellationToken ct = default);
}

public sealed class SnapshotRefreshResult
{
    /// <summary>succeeded / failed / snapshot_in_progress。</summary>
    public string Status { get; set; } = "failed";

    public long? SnapshotId { get; set; }
    public string? Error { get; set; }

    /// <summary>最终落库的快照（succeeded/failed 时非空；in_progress 时为进行中的记录）。</summary>
    public ServerSnapshot? Snapshot { get; set; }
}

/// <summary>
/// 快照编排：单飞限流 → 顺序执行采集器（事件流水逐条落库）→ 成功/失败状态落库 → 按保留策略裁剪。
/// 采集器整体超时由 options.TimeoutSeconds 兜底；调用方取消与超时都会把快照记为 Failed，不会留下 Running 孤儿。
/// </summary>
public class SnapshotService : ISnapshotService
{
    private readonly ISnapshotStore _store;
    private readonly ISshService _ssh;
    private readonly ICommandFilterService _filter;
    private readonly List<ISnapshotCollector> _collectors;
    private readonly IConfigService? _configService;
    private readonly IMySqlConnectionProvider? _mysqlProvider;

    /// <summary>每服务器单飞键（同一进程内）；配合 Store 初始化时的 Running 孤儿重置覆盖跨进程/崩溃场景。</summary>
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.OrdinalIgnoreCase);

    public SnapshotService(
        ISnapshotStore store,
        ISshService ssh,
        ICommandFilterService filter,
        IEnumerable<ISnapshotCollector> collectors)
        : this(store, ssh, filter, collectors, null, null)
    {
    }

    public SnapshotService(
        ISnapshotStore store,
        ISshService ssh,
        ICommandFilterService filter,
        IEnumerable<ISnapshotCollector> collectors,
        IConfigService? configService,
        IMySqlConnectionProvider? mysqlProvider)
    {
        _store = store;
        _ssh = ssh;
        _filter = filter;
        _collectors = collectors.OrderBy(c => c.Order).ToList();
        _configService = configService;
        _mysqlProvider = mysqlProvider;
    }

    /// <summary>记录本次快照实际使用的提权机制（采集器内部多次执行，取首个非 direct 值）。</summary>
    private sealed class EscalationRef
    {
        public string? Value;
    }

    public async Task<SnapshotRefreshResult> RefreshAsync(SshServerConfig server, SnapshotConfig options, CancellationToken ct = default)
    {
        if (!_active.TryAdd(server.Id, 0))
            return await InProgressAsync(server.Id, ct);

        var stopwatch = Stopwatch.StartNew();
        long snapshotId = 0;
        try
        {
            // 跨进程互斥：App 与 MCP 是两个进程, 各自的内存单飞锁互不可见, 这里用库里遗留的 Running 行兜底。
            // 超过"整体超时 + 宽限"仍是 Running 的视为上次崩溃/被杀遗留, 改判失败后继续, 避免永久卡死。
            var existing = await _store.GetRunningAsync(server.Id, ct);
            if (existing is not null)
            {
                if (!IsStale(existing, options))
                    return BuildInProgress(existing);

                await _store.FailAsync(existing.Id, "stale_running_snapshot (上一次采集未正常结束)", 0, null, ct);
                await SafeAppendEventAsync(existing.Id, server.Id, "failed", null, "stale_running_snapshot");
            }

            // 唯一部分索引兜底并发抢占: 抢到 null 说明另一进程刚插入 Running
            snapshotId = await _store.BeginAsync(server.Id, server.Name, ct) ?? 0;
            if (snapshotId == 0)
                return BuildInProgress(await _store.GetRunningAsync(server.Id, ct));

            var elevate = options.UseSudo && server.SudoType != SudoType.None;
            var escalationRef = new EscalationRef();
            var sections = new Dictionary<string, object?>();

            // 读取配置以按"数据源地址==本服务器"匹配 MySQL 数据源凭据（安全巡检核查 mysql.user 用）
            AppConfig? appConfig = null;
            if (_configService is not null)
            {
                try { appConfig = await _configService.LoadConfigAsync(); }
                catch { appConfig = null; }
            }

            var context = new SnapshotContext
            {
                Server = server,
                Elevate = elevate,
                ExecuteAsync = (command, useSudo, token) => ExecuteCoreAsync(server, command, useSudo, escalationRef, token),
                QueryMysqlAccountsAsync = appConfig is not null && _mysqlProvider is not null
                    ? token => ProbeMysqlAccountsAsync(appConfig, server, sections, token)
                    : null
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (options.TimeoutSeconds > 0)
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            var token = timeoutCts.Token;

            var abortedReason = default(string?);
            var attempted = 0;    // 实际执行过的采集器(不含 skipped)
            var valid = 0;        // 产出有效数据的采集器(ok/degraded)
            var firstError = default(string?);

            foreach (var collector in _collectors)
            {
                // 已中止(致命错误/超时/取消)：剩余采集器记 skipped 事件后跳过，不留黑盒
                if (abortedReason is not null || token.IsCancellationRequested)
                {
                    abortedReason ??= ct.IsCancellationRequested
                        ? "调用方已取消"
                        : $"快照超时(>{options.TimeoutSeconds}秒)";
                    sections[collector.Name] = SkippedSection(abortedReason);
                    await SafeAppendEventAsync(snapshotId, server.Id, "collector_skipped", collector.Name, abortedReason);
                    continue;
                }

                await SafeAppendEventAsync(snapshotId, server.Id, "collector_started", collector.Name, null);

                CollectorResult result;
                try
                {
                    result = await collector.CollectAsync(context, token);
                }
                catch (OperationCanceledException)
                {
                    result = CollectorResult.Fail("快照超时或已取消", "timeout");
                }
                catch (Exception ex)
                {
                    result = CollectorResult.Fail($"采集器异常: {ex.Message}", "unknown");
                }

                if (!result.Skipped)
                {
                    attempted++;
                    if (result.Success)
                        valid++;
                    else
                        firstError ??= result.Error;
                }

                var status = result.Skipped ? "skipped" : result.Success ? (result.Degraded ? "degraded" : "ok") : "failed";
                sections[collector.Name] = new Dictionary<string, object?>
                {
                    ["status"] = status,
                    ["durationMs"] = Math.Round(result.DurationMs, 1),
                    ["error"] = result.Success ? null : result.Error,
                    ["note"] = result.Note,
                    ["data"] = result.Data
                };

                await SafeAppendEventAsync(snapshotId, server.Id,
                    !result.Success ? "collector_failed" : result.Skipped ? "collector_skipped" : "collector_completed",
                    collector.Name,
                    result.Success ? result.Note : result.Error);

                // 传输层致命错误(认证/断网/超时/限流): 后续采集器必然同样失败, 立即中止
                if (!result.Success && result.IsFatal)
                    abortedReason = result.Error;
            }

            // 整体判定: 致命中止 → 失败; 无任何有效 section → 失败; 否则成功(部分降级记录在各 section)
            var shouldFail = abortedReason is not null || valid == 0;
            if (shouldFail)
            {
                var error = abortedReason ?? firstError ?? "所有采集器均未产生有效结果";
                await _store.FailAsync(snapshotId, error, stopwatch.Elapsed.TotalMilliseconds,
                    BuildDataJson(sections, elevate), CancellationToken.None);
                await SafeAppendEventAsync(snapshotId, server.Id, "failed", null, error);

                await TryPruneAsync(server.Id, options.RetentionPerServer);
                return new SnapshotRefreshResult
                {
                    Status = "failed",
                    SnapshotId = snapshotId,
                    Error = error,
                    Snapshot = await _store.GetByIdAsync(snapshotId, CancellationToken.None)
                };
            }

            var dataJson = BuildDataJson(sections, elevate);
            var escalation = escalationRef.Value ?? (elevate ? "unavailable" : "direct");
            await _store.CompleteAsync(snapshotId, dataJson, stopwatch.Elapsed.TotalMilliseconds,
                escalation, SnapshotMeta.CollectorVersion, CancellationToken.None);
            await SafeAppendEventAsync(snapshotId, server.Id, "completed", null,
                $"{valid}/{attempted} 采集器成功, 耗时 {stopwatch.Elapsed.TotalSeconds:F1}s");

            await TryPruneAsync(server.Id, options.RetentionPerServer);
            return new SnapshotRefreshResult
            {
                Status = "succeeded",
                SnapshotId = snapshotId,
                Snapshot = await _store.GetByIdAsync(snapshotId, CancellationToken.None)
            };
        }
        catch (Exception ex)
        {
            // 落库失败等意外: 尽力把 Running 改判为 Failed, 绝不留下悬挂状态
            if (snapshotId > 0)
            {
                try
                {
                    await _store.FailAsync(snapshotId, ex.Message, stopwatch.Elapsed.TotalMilliseconds, null, CancellationToken.None);
                    await _store.AppendEventAsync(snapshotId, server.Id, "failed", null, ex.Message, CancellationToken.None);
                }
                catch
                {
                    // 二次失败只吞掉: 已在 Process 退出时由 InitializeAsync 的孤儿重置兜底
                }
            }

            return new SnapshotRefreshResult
            {
                Status = "failed",
                SnapshotId = snapshotId > 0 ? snapshotId : null,
                Error = ex.Message
            };
        }
        finally
        {
            _active.TryRemove(server.Id, out _);
        }
    }

    private async Task<SnapshotRefreshResult> InProgressAsync(string serverId, CancellationToken ct)
    {
        var running = await _store.GetRunningAsync(serverId, ct);
        return BuildInProgress(running);
    }

    private static SnapshotRefreshResult BuildInProgress(ServerSnapshot? running) => new()
    {
        Status = "snapshot_in_progress",
        SnapshotId = running?.Id,
        Snapshot = running,
        Error = running is null
            ? "该服务器已有快照在采集中"
            : $"该服务器已有快照在采集中(快照#{running.Id}), 请稍后查询"
    };

    /// <summary>库里的 Running 行是否已陈旧（上次进程崩溃遗留）：超过整体超时 + 90s 宽限即视为可回收。</summary>
    private static bool IsStale(ServerSnapshot running, SnapshotConfig options)
    {
        if (!DateTime.TryParse(running.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created))
            return true;
        var limit = TimeSpan.FromSeconds(Math.Max(options.TimeoutSeconds, 60) + 90);
        return DateTime.UtcNow - created > limit;
    }

    /// <summary>
    /// 用"与该服务器匹配的已配置 MySQL 数据源凭据"读取 mysql.user。返回 null 表示没有匹配的数据源
    /// （采集器据此回退到免密 best-effort）。匹配规则：
    ///   - 数据源 Host 为本机回环(localhost/127.0.0.1/::1)：其 SSH 隧道服务器 == 本服务器时匹配；
    ///   - 数据源 Host 为非回环地址：与本服务器 host 或采集到的本机 IP 相同即匹配（无论是否配置隧道）。
    /// 账号非 root 也能查，但必须拥有 mysql.* 的 SELECT 权限，否则返回 Checked=false 并说明原因。
    /// </summary>
    private async Task<MysqlAuditProbeResult?> ProbeMysqlAccountsAsync(
        AppConfig config, SshServerConfig server, Dictionary<string, object?> sections, CancellationToken ct)
    {
        var ips = ExtractServerIps(sections);
        var candidates = config.DataSources
            .Where(ds => ds.Type.Equals("mysql", StringComparison.OrdinalIgnoreCase))
            .Where(ds => MatchesServer(ds, server, ips))
            .OrderByDescending(ds => ds.Username.Equals("root", StringComparison.OrdinalIgnoreCase))   // 优先 root(更可能有权限)
            .ToList();
        if (candidates.Count == 0)
            return null;

        MysqlAuditProbeResult? last = null;
        foreach (var ds in candidates)
        {
            var result = await QueryMysqlAccountsAsync(ds, ct);
            if (result.Checked)
                return result;
            last = result;
        }
        return last;
    }

    /// <summary>
    /// 数据源是否"就在该服务器上"（用于决定能否用其实例凭据核查该机 MySQL）：
    /// 回环 host 且隧道服务器==本服务器；或非回环 host == 本服务器 host / 采集到的本机 IP。
    /// </summary>
    public static bool MatchesServer(DataSourceConfig ds, SshServerConfig server, IReadOnlyCollection<string> serverIps)
    {
        var host = (ds.Host ?? string.Empty).Trim();
        if (IsLoopbackHost(host))
            return ds.AccessMode == AccessMode.SshTunnel &&
                   string.Equals(ds.TunnelServerId, server.Id, StringComparison.OrdinalIgnoreCase);

        return HostEquals(host, server.Host) || serverIps.Any(ip => HostEquals(host, ip));
    }

    private async Task<MysqlAuditProbeResult> QueryMysqlAccountsAsync(DataSourceConfig ds, CancellationToken ct)
    {
        try
        {
            using var session = await _mysqlProvider!.OpenAsync(ds, ct);

            var accounts = new List<(string, string)>();
            await using (var command = session.Connection.CreateCommand())
            {
                command.CommandText = "SELECT user, host FROM mysql.user";
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    accounts.Add((reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                                  reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
            }

            // 取各账户的授权明细, 用于识别"可远程登录的高权账户"（SHOW GRANTS FOR 'user'@'host'）
            var remoteGrants = new List<MysqlRemoteGrant>();
            foreach (var (user, host) in accounts)
            {
                var grants = new List<string>();
                try
                {
                    await using var grantCommand = session.Connection.CreateCommand();
                    grantCommand.CommandText = $"SHOW GRANTS FOR '{user.Replace("'", "''")}'@'{host.Replace("'", "''")}'";
                    await using var grantReader = await grantCommand.ExecuteReaderAsync(ct);
                    while (await grantReader.ReadAsync(ct))
                        grants.Add(grantReader.GetString(0));
                }
                catch
                {
                    // 单个账户授权读取失败忽略（不影响账户清单结论）
                }

                if (grants.Count > 0)
                    remoteGrants.Add(new MysqlRemoteGrant(user, host, grants));
            }

            return new MysqlAuditProbeResult(true, null, accounts, remoteGrants);
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            var reason = message.Contains("denied", StringComparison.OrdinalIgnoreCase)
                ? $"数据源账号无 mysql.* 查询权限(需 SELECT ON mysql.* 才能枚举账户): {Shorten(message)}"
                : $"数据源核查失败: {Shorten(message)}";
            return new MysqlAuditProbeResult(false, reason, new List<(string, string)>(), new List<MysqlRemoteGrant>());
        }
    }

    private static IReadOnlyCollection<string> ExtractServerIps(Dictionary<string, object?> sections)
    {
        if (sections.TryGetValue("resource", out var resource) &&
            resource is Dictionary<string, object?> section &&
            section.TryGetValue("data", out var data) &&
            data is Dictionary<string, object?> resourceData &&
            resourceData.TryGetValue("ips", out var ips) &&
            ips is string[] array)
            return array;
        return Array.Empty<string>();
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host is "127.0.0.1" or "::1" ||
        host.StartsWith("127.", StringComparison.Ordinal);

    private static bool HostEquals(string a, string? b) =>
        !string.IsNullOrWhiteSpace(b) && a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string text, int max = 200) =>
        text.Length <= max ? text : text[..max] + "...";

    private async Task<CommandResult> ExecuteCoreAsync(
        SshServerConfig server, string command, bool elevate, EscalationRef escalationRef, CancellationToken ct)
    {
        // 快照命令全部为内置固定只读命令, 但用户命令过滤器的禁止规则仍必须生效
        if (_filter.CheckCommand(command) == CommandFilterResult.Blocked)
            return new CommandResult
            {
                Success = false,
                Error = "采集命令被安全策略禁止(CommandFilter.Blocked)",
                ErrorKind = "blocked"
            };

        try
        {
            var result = elevate
                ? await _ssh.ExecuteWithSudoAsync(server, command, ct)
                : await _ssh.ExecuteCommandAsync(server, command, ct, 60);
            if (result.Escalation is not null && result.Escalation != "direct")
                escalationRef.Value ??= result.Escalation;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CommandResult { Success = false, Error = ex.Message, ErrorKind = "unknown" };
        }
    }

    private static string BuildDataJson(Dictionary<string, object?> sections, bool elevated) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["collectorVersion"] = SnapshotMeta.CollectorVersion,
            ["elevated"] = elevated,
            ["sections"] = sections
        });

    private static Dictionary<string, object?> SkippedSection(string reason) => new()
    {
        ["status"] = "skipped",
        ["durationMs"] = 0,
        ["error"] = null,
        ["note"] = reason,
        ["data"] = null
    };

    /// <summary>事件落库失败不能中断快照（审计/历史是增强项, 主流程状态以 Snapshots 行为准）。</summary>
    private async Task SafeAppendEventAsync(long snapshotId, string serverId, string kind, string? collector, string? message)
    {
        try
        {
            await _store.AppendEventAsync(snapshotId, serverId, kind, collector, message, CancellationToken.None);
        }
        catch
        {
            // 吞掉: 见方法注释
        }
    }

    private async Task TryPruneAsync(string serverId, int retentionPerServer)
    {
        try
        {
            await _store.PruneAsync(serverId, retentionPerServer, CancellationToken.None);
        }
        catch
        {
            // 裁剪失败不影响本次快照结果(下次刷新会再试)
        }
    }
}
