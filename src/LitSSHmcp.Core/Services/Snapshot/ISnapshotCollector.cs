using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.Core.Services.Snapshot;

/// <summary>
/// 快照元数据。新增/调整采集维度、或 section 数据结构不兼容变更时递增 <see cref="CollectorVersion"/>，
/// 落库随快照记录保存，历史趋势解析据此区分口径。
/// </summary>
public static class SnapshotMeta
{
    public const int CollectorVersion = 1;
}

/// <summary>
/// 一个可插拔的快照采集维度。新增维度 = 实现本接口 + 在 DI 注册一个 <c>ISnapshotCollector</c>，
/// 无需改快照主流程；未来定时采集/趋势图直接复用同一批采集器与历史 DataJson。
/// </summary>
public interface ISnapshotCollector
{
    /// <summary>section 名（落库 DataJson.sections 的键），稳定不可随意改名（历史数据按此键读取）。</summary>
    string Name { get; }

    /// <summary>执行顺序（小者先）。</summary>
    int Order { get; }

    Task<CollectorResult> CollectAsync(SnapshotContext context, CancellationToken ct);
}

/// <summary>采集器执行上下文：目标服务器 + 提权决策 + 统一的远程执行入口（已过命令过滤器/限流/审计路径）。</summary>
public sealed class SnapshotContext
{
    public required SshServerConfig Server { get; init; }

    /// <summary>本次快照是否允许提权（snapshot.useSudo && 服务器配置了 SudoType）。采集器按需选用。</summary>
    public required bool Elevate { get; init; }

    /// <summary>执行远程命令（elevate=true 走 sudo/su 通道）。结果已脱敏。</summary>
    public required Func<string, bool, CancellationToken, Task<CommandResult>> ExecuteAsync { get; init; }

    /// <summary>
    /// 可选：用"与该服务器匹配的已配置 MySQL 数据源凭据"查询 mysql.user（安全巡检用）。
    /// null = 本进程未提供该能力或没有匹配的数据源；返回 null = 无匹配数据源（采集器回退到免密 best-effort）。
    /// </summary>
    public Func<CancellationToken, Task<MysqlAuditProbeResult?>>? QueryMysqlAccountsAsync { get; init; }
}

/// <summary>
/// MySQL 账户核查结果。<see cref="Checked"/>=false 表示能连但读取 mysql.user 失败（多为账号无 mysql.* 权限或登录失败），
/// <see cref="Reason"/> 说明原因；true 时 <see cref="Accounts"/> 为全部 user/host，<see cref="RemoteGrants"/> 为各账户的授权明细。
/// </summary>
public sealed record MysqlAuditProbeResult(
    bool Checked,
    string? Reason,
    List<(string User, string Host)> Accounts,
    List<MysqlRemoteGrant> RemoteGrants);

/// <summary>某账户的授权明细（SHOW GRANTS 每行一条），用于识别"可远程登录的高权账户"。</summary>
public sealed record MysqlRemoteGrant(string User, string Host, List<string> Grants);

/// <summary>采集器结果：ok / degraded(成功但降级) / skipped(环境不具备, 如无 nginx) / failed。record 以便用 <c>with</c> 补充耗时。</summary>
public sealed record CollectorResult
{
    public bool Success { get; set; }
    public bool Skipped { get; set; }
    public bool Degraded { get; set; }
    public string? Error { get; set; }
    public string? Note { get; set; }

    /// <summary>失败时的分类（auth/network/timeout/rate_limited/blocked/...），供主流程判定是否整体中止。</summary>
    public string? ErrorKind { get; set; }
    public object? Data { get; set; }
    public double DurationMs { get; set; }

    public static CollectorResult Ok(object? data, bool degraded = false, string? note = null) =>
        new() { Success = true, Degraded = degraded, Note = note, Data = data };

    public static CollectorResult Fail(string error, string? errorKind = null) =>
        new() { Success = false, Error = error, ErrorKind = errorKind };

    public static CollectorResult Skip(string reason) =>
        new() { Success = true, Skipped = true, Note = reason };

    /// <summary>传输层致命错误（认证/网络/超时/限流）——后续采集器同样会失败，主流程应中止而非逐个撞墙。</summary>
    public bool IsFatal => ErrorKind is "auth" or "host_key" or "network" or "timeout" or "rate_limited";

    /// <summary>从命令结果构造失败项（exit!=0 为普通失败，ErrorKind 为传输层分类）。</summary>
    public static CollectorResult FromCommandFailure(CommandResult result) =>
        Fail(string.IsNullOrWhiteSpace(result.Error) ? $"命令失败 (exit {result.ExitCode})" : result.Error, result.ErrorKind);
}
