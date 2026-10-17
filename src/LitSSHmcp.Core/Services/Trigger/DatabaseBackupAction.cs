using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using MySqlConnector;
using Npgsql;

namespace LitSSHmcp.Core.Services.Trigger;

/// <summary>
/// 数据库备份动作：备份 MySQL / PostgreSQL（整库或指定表），产物为 SQL 转储（可选 gzip 为 .sql.gz），
/// 文件名 {触发事件名}-{数据源}-{库}-{yyyyMMdd-HHmmss}.sql[.gz]；完成后按 RetentionCount 清理最旧备份。
///
/// 策略：优先调用外部 dump 工具（mysqldump / pg_dump，在运行 App 的本机执行，经 SSH 隧道时自动建隧道）；
/// 探测不到工具时退化为托管流式导出（边读边写 INSERT，绝不整表驻留内存）。
/// 全程受全局并发闸约束（security.limits.maxConcurrentBackups），避免多任务同时 dump 压垮本机/目标。
/// 注意：数据库备份始终在**本机**执行并落盘，不受触发事件「执行目标」影响。
/// </summary>
public static class DatabaseBackupAction
{
    private const int TimeoutSeconds = 3600;
    private const int InsertBatchSize = 200;

    public static async Task<TriggerActionResult> RunAsync(TriggerActionContext ctx, CancellationToken ct = default)
    {
        var action = ctx.Action;
        var sw = Stopwatch.StartNew();
        var result = new TriggerActionResult { Type = TriggerActionType.DatabaseBackup };

        var config = ctx.Config;
        if (config is null)
        {
            result.Error = "缺少应用配置，无法解析数据源";
            return Finish(result, sw);
        }

        var ds = (config.DataSources ?? Array.Empty<DataSourceConfig>())
            .FirstOrDefault(d => string.Equals(d.Id, action.DatasourceId, StringComparison.OrdinalIgnoreCase));
        if (ds is null)
        {
            result.Error = $"数据源不存在: {action.DatasourceId}（请重新选择）";
            return Finish(result, sw);
        }

        var type = (ds.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (type != "mysql" && type != "postgres")
        {
            result.Error = $"数据库备份仅支持 mysql / postgres（当前数据源类型: {ds.Type}）";
            return Finish(result, sw);
        }

        var destDir = action.DestDir.TrimEnd('/', '\\');
        if (string.IsNullOrWhiteSpace(destDir))
        {
            result.Error = "数据库备份动作缺少输出目录";
            return Finish(result, sw);
        }

        var database = ResolveDatabase(type, ds, action.Database);
        if (type == "mysql" && string.IsNullOrWhiteSpace(database))
        {
            result.Error = "MySQL 备份需要指定库名（数据源未设置默认库）";
            return Finish(result, sw);
        }

        var maxConcurrent = config.Security?.Limits?.MaxConcurrentBackups ?? 2;
        IDisposable gate;
        try
        {
            gate = await BackupConcurrency.AcquireAsync(maxConcurrent, ct);
        }
        catch (OperationCanceledException)
        {
            result.Error = "已取消";
            return Finish(result, sw);
        }

        try
        {
            var tables = NormalizeTables(action.Tables);

            if (ctx.Server is not null)
            {
                // 执行目标=远程服务器：在服务器上执行 dump 并落盘到服务器目录
                var remote = await RunRemoteAsync(ctx, ds, type, database, tables, destDir, action.GzipEnabled, action.RetentionCount, ct);
                result.Success = remote.Success;
                if (remote.Success) result.Output = remote.Output;
                else result.Error = remote.Error;
            }
            else
            {
                var gzip = action.GzipEnabled;
                var fileName = BuildFileName(ctx.Trigger.Name, ds.Name, database, gzip);

                var external = await ResolveDumpToolAsync(type, action.DumpToolPath, ct);
                (bool Success, string Output, string Error) outcome;
                if (external is not null)
                {
                    outcome = await RunExternalAsync(ctx, ds, type, database, tables, destDir, fileName, external, gzip, ct);
                    if (!outcome.Success && LooksLikeToolProblem(outcome.Error))
                    {
                        // 外部 dump 工具不可用/参数不兼容 → 退化托管流式导出
                        var managed = await RunManagedAsync(ctx, ds, type, database, tables, destDir, fileName, gzip, ct);
                        if (managed.Success)
                            outcome = (true, managed.Output + "（外部 dump 工具不可用，已退化托管导出）", string.Empty);
                        else
                            outcome.Error = outcome.Error + "；托管导出亦失败: " + managed.Error;
                    }
                }
                else
                {
                    outcome = await RunManagedAsync(ctx, ds, type, database, tables, destDir, fileName, gzip, ct);
                }

                result.Success = outcome.Success;
                if (result.Success)
                {
                    var saved = Cleanup(destDir, ctx.Trigger.Name, gzip ? "sql.gz" : "sql", action.RetentionCount);
                    result.Output = outcome.Output + (string.IsNullOrWhiteSpace(saved) ? string.Empty : "；" + saved);
                }
                else
                {
                    result.Error = outcome.Error;
                }
            }
        }
        catch (OperationCanceledException)
        {
            result.Error = "已取消";
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        finally
        {
            gate.Dispose();
        }

        return Finish(result, sw);
    }

    private static TriggerActionResult Finish(TriggerActionResult result, Stopwatch sw)
    {
        result.Duration = sw.Elapsed;
        return result;
    }

    // ---------- 外部 dump 工具路径 ----------

    private static async Task<(bool Success, string Output, string Error)> RunExternalAsync(
        TriggerActionContext ctx, DataSourceConfig ds, string type, string database, IReadOnlyList<string> tables,
        string destDir, string fileName, string tool, bool gzip, CancellationToken ct)
    {
        var endpoint = await ResolveEndpointAsync(ds, ctx.Config!, ct);
        if (endpoint.Error is not null)
            return (false, string.Empty, endpoint.Error);

        try
        {
            Directory.CreateDirectory(destDir);
            var outFile = Path.Combine(destDir, fileName);

            var args = new List<string>();
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (type == "mysql")
            {
                args.AddRange(BuildMysqlArgs(endpoint.Host, endpoint.Port, ds.Username, database, tables));
                if (!string.IsNullOrEmpty(ds.Password))
                    env["MYSQL_PWD"] = ds.Password!;
            }
            else
            {
                args.AddRange(BuildPgArgs(endpoint.Host, endpoint.Port, ds.Username, database, tables));
                if (!string.IsNullOrEmpty(ds.Password))
                    env["PGPASSWORD"] = ds.Password!;
            }

            bool ok;
            string error;
            await using (var fs = new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                if (gzip)
                {
                    await using var gz = new GZipStream(fs, CompressionLevel.Fastest);
                    var r = await LocalProcessRunner.RunToStreamAsync(tool, args, gz, TimeoutSeconds, ct, environment: env);
                    ok = r.Success;
                    error = r.Error;
                }
                else
                {
                    var r = await LocalProcessRunner.RunToStreamAsync(tool, args, fs, TimeoutSeconds, ct, environment: env);
                    ok = r.Success;
                    error = r.Error;
                }
            }

            if (!ok)
            {
                TryDelete(outFile);
                return (false, string.Empty, $"{Path.GetFileName(tool)} 失败: {error}");
            }

            var size = new FileInfo(outFile).Length;
            return (true, $"已备份到 {outFile}（{FormatSize(size)}，{DescribeScope(tables)}）", string.Empty);
        }
        catch (OperationCanceledException)
        {
            return (false, string.Empty, "已取消");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
        finally
        {
            endpoint.Tunnel?.Dispose();
        }
    }

    /// <summary>构建 mysqldump 参数（不包含密码，密码经 MYSQL_PWD 环境变量传入）。</summary>
    public static IReadOnlyList<string> BuildMysqlArgs(string host, int port, string user, string database, IReadOnlyList<string> tables)
    {
        var args = new List<string>
        {
            $"--host={host}",
            $"--port={port.ToString(CultureInfo.InvariantCulture)}",
            $"--user={user}",
            "--default-character-set=utf8mb4",
            "--single-transaction",
            "--quick",
            "--skip-lock-tables",
            "--routines",
            "--events",
            "--triggers",
            "--hex-blob",
            "--set-charset",
            "--no-tablespaces"
        };
        if (tables.Count == 0)
        {
            args.Add("--databases");
            args.Add(database);
        }
        else
        {
            args.Add(database);
            args.AddRange(tables);
        }
        return args;
    }

    /// <summary>构建 pg_dump 参数（不包含密码，密码经 PGPASSWORD 环境变量传入）。</summary>
    public static IReadOnlyList<string> BuildPgArgs(string host, int port, string user, string database, IReadOnlyList<string> tables)
    {
        var args = new List<string>
        {
            $"--host={host}",
            $"--port={port.ToString(CultureInfo.InvariantCulture)}",
            $"--username={user}",
            "--no-password",
            "--clean",
            "--if-exists",
            "--no-owner",
            "--no-privileges",
            "--encoding=UTF8"
        };
        foreach (var t in tables)
        {
            args.Add("--table");
            args.Add(t);
        }
        args.Add(database);
        return args;
    }

    private static async Task<string?> ResolveDumpToolAsync(string type, string configuredPath, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return configuredPath.Trim();

        var tool = type == "mysql" ? "mysqldump" : "pg_dump";
        return await LocalProcessRunner.ExistsAsync(tool, ct) ? tool : null;
    }

    /// <summary>判断失败是否属于"工具缺失/参数不兼容"（可退化托管导出），而非连通性/权限等真实错误。</summary>
    private static bool LooksLikeToolProblem(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return false;
        return error.Contains("unknown option", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("unrecognized", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("not recognized", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("cannot find", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("no such file", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("找不到", StringComparison.Ordinal) ||
               error.Contains("系统找不到", StringComparison.Ordinal) ||
               error.Contains("不是内部或外部命令", StringComparison.Ordinal);
    }

    // ---------- 远程服务器执行（执行目标=远程） ----------

    /// <summary>
    /// 构建在服务器上执行的 dump 命令（密码经环境变量前缀传入，不进入 dump 进程 argv）。
    /// gzip=true 时管道到 `gzip`。所有参数经 POSIX shell 单引号转义。
    /// </summary>
    public static string BuildRemoteDumpCommand(string type, string tool, string? password, string host, int port,
        string user, string database, IReadOnlyList<string> tables, string outFile, bool gzip)
    {
        var q = GitAction.ShellQuote;
        var args = type == "mysql"
            ? BuildMysqlArgs(host, port, user, database, tables)
            : BuildPgArgs(host, port, user, database, tables);
        var argStr = string.Join(" ", args.Select(q));
        var envPrefix = string.IsNullOrEmpty(password)
            ? string.Empty
            : (type == "mysql" ? $"MYSQL_PWD={q(password!)} " : $"PGPASSWORD={q(password!)} ");
        var redirect = gzip ? " | gzip" : string.Empty;
        return $"{envPrefix}{q(tool)} {argStr}{redirect} > {q(outFile)}";
    }

    private static async Task<(bool Success, string Output, string Error)> RunRemoteAsync(
        TriggerActionContext ctx, DataSourceConfig ds, string type, string database, IReadOnlyList<string> tables,
        string destDir, bool gzip, int retentionCount, CancellationToken ct)
    {
        var server = ctx.Server!;
        var ssh = ctx.Ssh!;
        var q = GitAction.ShellQuote;

        try
        {
            var tool = string.IsNullOrWhiteSpace(ctx.Action.DumpToolPath)
                ? (type == "mysql" ? "mysqldump" : "pg_dump")
                : ctx.Action.DumpToolPath.Trim();

            var probe = await ssh.ExecuteCommandAsync(server, $"command -v {q(tool)} >/dev/null 2>&1", ct, timeoutSeconds: 30);
            if (!probe.Success)
                return (false, string.Empty,
                    $"目标服务器「{server.Name}」缺少 {tool}（远程数据库备份需在服务器上安装 MySQL/PostgreSQL 客户端工具，或在动作里指定 dump 工具路径）");

            var useGzip = gzip;
            if (useGzip)
            {
                var gzProbe = await ssh.ExecuteCommandAsync(server, "command -v gzip >/dev/null 2>&1", ct, timeoutSeconds: 30);
                if (!gzProbe.Success) useGzip = false;
            }

            var fileName = BuildFileName(ctx.Trigger.Name, ds.Name, database, useGzip);
            var outFile = $"{destDir.TrimEnd('/')}/{fileName}";

            var inner = BuildRemoteDumpCommand(type, tool, ds.Password, ds.Host, ds.Port, ds.Username, database, tables, outFile, useGzip);

            // 有 bash 时用 pipefail，dump 失败即整体失败；否则直接执行
            var hasBash = (await ssh.ExecuteCommandAsync(server, "command -v bash >/dev/null 2>&1", ct, timeoutSeconds: 30)).Success;
            var run = hasBash
                ? $"mkdir -p {q(destDir)} && bash -o pipefail -c {q(inner)}"
                : $"mkdir -p {q(destDir)} && {inner}";

            var exec = await ssh.ExecuteCommandAsync(server, run, ct, timeoutSeconds: TimeoutSeconds);
            if (!exec.Success)
                return (false, string.Empty, $"{tool} 失败: {(string.IsNullOrWhiteSpace(exec.Error) ? exec.Output : exec.Error)}");

            long size = 0;
            var sizeR = await ssh.ExecuteCommandAsync(server, $"wc -c < {q(outFile)}", ct, timeoutSeconds: 60);
            if (sizeR.Success && long.TryParse(sizeR.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out var parsed))
                size = parsed;

            var saved = await CleanupRemoteAsync(ssh, server, destDir, ctx.Trigger.Name, useGzip ? "sql.gz" : "sql", retentionCount, ct);

            var note = gzip && !useGzip ? "（服务器缺少 gzip，已输出未压缩 .sql）" : string.Empty;
            var output = $"已备份到 {server.Name}:{outFile}（{FormatSize(size)}，{DescribeScope(tables)}）{note}" +
                         (string.IsNullOrWhiteSpace(saved) ? string.Empty : "；" + saved);
            return (true, output, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return (false, string.Empty, "已取消");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }

    private static async Task<string> CleanupRemoteAsync(ISshService ssh, SshServerConfig server, string destDir,        string triggerName, string ext, int retentionCount, CancellationToken ct)
    {
        if (retentionCount <= 0)
            return string.Empty;

        try
        {
            var q = GitAction.ShellQuote;
            var prefix = SafeName(triggerName);
            var cmd = $"cd {q(destDir)} && ls -1t -- {q(prefix)}-*.{ext} 2>/dev/null | tail -n +{retentionCount + 1} | xargs -r -I{{}} rm -f -- {{}}";
            await ssh.ExecuteCommandAsync(server, cmd, ct, timeoutSeconds: 60);
            return string.Empty; // 清理失败不视为整体失败
        }
        catch
        {
            return string.Empty;
        }
    }

    // ---------- 端点解析（直连 / SSH 隧道） ----------

    private static async Task<(string Host, int Port, SshTunnel? Tunnel, string? Error)> ResolveEndpointAsync(
        DataSourceConfig ds, AppConfig config, CancellationToken ct)
    {
        if (ds.AccessMode != AccessMode.SshTunnel)
            return (ds.Host, ds.Port, null, null);

        var server = TunnelServerResolver.Resolve(config, ds);
        if (server is null)
            return (string.Empty, 0, null, $"数据源 '{ds.Name}' {TunnelServerResolver.MissingServerMessage}");
        if (server.Disabled)
            return (string.Empty, 0, null, $"数据源 '{ds.Name}' {TunnelServerResolver.DisabledMessage(server)}");

        try
        {
            var knownHosts = new FileSshKnownHostsStore();
            var mode = new SecurityOptionsProvider().SshHostKey.Mode;
            var tunnel = await SshTunnel.StartAsync(server, ds.Host, ds.Port, ct, knownHosts, mode);
            return (IPAddress.Loopback.ToString(), (int)tunnel.LocalPort, tunnel, null);
        }
        catch (Exception ex)
        {
            return (string.Empty, 0, null, "建立 SSH 隧道失败: " + ex.Message);
        }
    }

    // ---------- 托管流式兜底 ----------

    private static Task<(bool Success, string Output, string Error)> RunManagedAsync(
        TriggerActionContext ctx, DataSourceConfig ds, string type, string database, IReadOnlyList<string> tables,
        string destDir, string fileName, bool gzip, CancellationToken ct)
        => type == "mysql"
            ? ManagedMySqlAsync(ctx, ds, database, tables, destDir, fileName, gzip, ct)
            : ManagedPostgresAsync(ctx, ds, database, tables, destDir, fileName, gzip, ct);

    private static async Task<(bool Success, string Output, string Error)> ManagedMySqlAsync(
        TriggerActionContext ctx, DataSourceConfig ds, string database, IReadOnlyList<string> tables,
        string destDir, string fileName, bool gzip, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(destDir);
            var outFile = Path.Combine(destDir, fileName);
            var configService = new ConfigService();
            var security = new SecurityOptionsProvider();
            var limiter = new TargetLimiter(security, ConfigPaths.TargetStateDir);
            var provider = new MySqlConnectionProvider(configService, new FileSshKnownHostsStore(), security, limiter);

            using var session = await provider.OpenAsync(ds, ct);
            var conn = session.Connection;

            var tableList = tables.Count > 0 ? tables.ToList() : await ListMySqlTablesAsync(conn, database, ct);

            await using var fs = new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            await using var target = (Stream)(gzip ? new GZipStream(fs, CompressionLevel.Fastest) : fs);
            await using var writer = new StreamWriter(target, new UTF8Encoding(false));

            await writer.WriteLineAsync("-- LitSSH MCP 托管导出（未使用 mysqldump，DDL 为 SHOW CREATE TABLE 结果）");
            await writer.WriteLineAsync($"-- 数据源: {ds.Name}  库: {database}  时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            await writer.WriteLineAsync("SET NAMES utf8mb4;");
            await writer.WriteLineAsync("SET FOREIGN_KEY_CHECKS=0;");
            await writer.WriteLineAsync();

            foreach (var table in tableList)
            {
                ct.ThrowIfCancellationRequested();
                await WriteMySqlTableAsync(conn, database, table, writer, ct);
                await writer.WriteLineAsync();
            }

            await writer.WriteLineAsync("SET FOREIGN_KEY_CHECKS=1;");
            await writer.FlushAsync();

            var size = new FileInfo(outFile).Length;
            return (true, $"已备份到 {outFile}（{FormatSize(size)}，托管导出，{DescribeScope(tables)}）", string.Empty);
        }
        catch (OperationCanceledException)
        {
            return (false, string.Empty, "已取消");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, "托管导出失败: " + ex.Message);
        }
    }

    private static async Task<List<string>> ListMySqlTablesAsync(MySqlConnection conn, string database, CancellationToken ct)
    {
        var list = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=@db AND TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME";
        cmd.Parameters.AddWithValue("@db", database);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(reader.GetString(0));
        return list;
    }

    private static async Task WriteMySqlTableAsync(MySqlConnection conn, string database, string table, StreamWriter writer, CancellationToken ct)
    {
        await using (var ddlCmd = conn.CreateCommand())
        {
            ddlCmd.CommandText = $"SHOW CREATE TABLE {MySqlQuote(database)}.{MySqlQuote(table)}";
            await using var reader = await ddlCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return;
            var ddl = reader.GetString(1);
            await writer.WriteLineAsync($"DROP TABLE IF EXISTS {MySqlQuote(table)};");
            await writer.WriteLineAsync(ddl + ";");
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {MySqlQuote(database)}.{MySqlQuote(table)}";
        await using var data = await cmd.ExecuteReaderAsync(ct);

        var columns = Enumerable.Range(0, data.FieldCount).Select(data.GetName).ToArray();
        var columnSql = string.Join(", ", columns.Select(MySqlQuote));
        var rows = new List<string>();

        while (await data.ReadAsync(ct))
        {
            var values = new string[columns.Length];
            for (var i = 0; i < columns.Length; i++)
                values[i] = data.IsDBNull(i) ? "NULL" : MySqlLiteral(data.GetValue(i));
            rows.Add("(" + string.Join(", ", values) + ")");

            if (rows.Count >= InsertBatchSize)
            {
                await writer.WriteLineAsync($"INSERT INTO {MySqlQuote(table)} ({columnSql}) VALUES");
                await writer.WriteLineAsync(string.Join(",\n", rows) + ";");
                rows.Clear();
            }
        }

        if (rows.Count > 0)
        {
            await writer.WriteLineAsync($"INSERT INTO {MySqlQuote(table)} ({columnSql}) VALUES");
            await writer.WriteLineAsync(string.Join(",\n", rows) + ";");
        }
        await writer.FlushAsync();
    }

    private static async Task<(bool Success, string Output, string Error)> ManagedPostgresAsync(
        TriggerActionContext ctx, DataSourceConfig ds, string database, IReadOnlyList<string> tables,
        string destDir, string fileName, bool gzip, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(destDir);
            var outFile = Path.Combine(destDir, fileName);
            var configService = new ConfigService();
            var security = new SecurityOptionsProvider();
            var limiter = new TargetLimiter(security, ConfigPaths.TargetStateDir);
            var provider = new PostgresConnectionProvider(configService, new FileSshKnownHostsStore(), security, limiter);

            using var session = await provider.OpenAsync(ds, ct);
            var conn = session.Connection;

            await using var fs = new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            await using var target = (Stream)(gzip ? new GZipStream(fs, CompressionLevel.Fastest) : fs);
            await using var writer = new StreamWriter(target, new UTF8Encoding(false));

            await writer.WriteLineAsync("-- LitSSH MCP 托管导出（未使用 pg_dump，DDL 由 information_schema 生成）");
            await writer.WriteLineAsync($"-- 数据源: {ds.Name}  库: {database}  时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            await writer.WriteLineAsync("SET client_encoding = 'UTF8';");
            await writer.WriteLineAsync("SET standard_conforming_strings = on;");
            await writer.WriteLineAsync();

            var tableList = tables.Count > 0
                ? tables.Select(SplitPgTable).ToList()
                : await ListPgTablesAsync(conn, ct);

            foreach (var (schema, table) in tableList)
            {
                ct.ThrowIfCancellationRequested();
                await WritePgTableAsync(conn, schema, table, writer, ct);
                await writer.WriteLineAsync();
            }

            await writer.FlushAsync();

            var size = new FileInfo(outFile).Length;
            return (true, $"已备份到 {outFile}（{FormatSize(size)}，托管导出，{DescribeScope(tables)}）", string.Empty);
        }
        catch (OperationCanceledException)
        {
            return (false, string.Empty, "已取消");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, "托管导出失败: " + ex.Message);
        }
    }

    private static async Task<List<(string Schema, string Table)>> ListPgTablesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var list = new List<(string, string)>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT table_schema, table_name FROM information_schema.tables " +
                          "WHERE table_type='BASE TABLE' AND table_schema NOT IN ('pg_catalog','information_schema') " +
                          "ORDER BY table_schema, table_name";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add((reader.GetString(0), reader.GetString(1)));
        return list;
    }

    private static async Task WritePgTableAsync(NpgsqlConnection conn, string schema, string table, StreamWriter writer, CancellationToken ct)
    {
        var columns = new List<string>();
        var defs = new List<string>();
        await using (var meta = conn.CreateCommand())
        {
            meta.CommandText = "SELECT column_name, data_type, udt_name, character_maximum_length, " +
                               "numeric_precision, numeric_scale, is_nullable, column_default " +
                               "FROM information_schema.columns WHERE table_schema=@s AND table_name=@t ORDER BY ordinal_position";
            meta.Parameters.AddWithValue("@s", schema);
            meta.Parameters.AddWithValue("@t", table);
            await using var reader = await meta.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                var dataType = reader.GetString(1);
                var udt = reader.GetString(2);
                var charLen = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);
                var numPrecision = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4);
                var numScale = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5);
                var nullable = reader.GetString(6).Equals("YES", StringComparison.OrdinalIgnoreCase);
                var def = reader.IsDBNull(7) ? null : Convert.ToString(reader.GetValue(7), CultureInfo.InvariantCulture);

                columns.Add(PgQuote(name));
                var sb = new StringBuilder();
                sb.Append(PgQuote(name)).Append(' ').Append(MapPgType(dataType, udt, charLen, numPrecision, numScale));
                if (def is not null) sb.Append(" DEFAULT ").Append(def);
                if (!nullable) sb.Append(" NOT NULL");
                defs.Add(sb.ToString());
            }
        }

        await writer.WriteLineAsync($"DROP TABLE IF EXISTS {PgQuote(schema)}.{PgQuote(table)} CASCADE;");
        await writer.WriteLineAsync($"CREATE TABLE {PgQuote(schema)}.{PgQuote(table)} ({string.Join(", ", defs)});");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {PgQuote(schema)}.{PgQuote(table)}";
        await using var data = await cmd.ExecuteReaderAsync(ct);

        var columnList = string.Join(", ", columns);
        var rows = new List<string>();
        while (await data.ReadAsync(ct))
        {
            var values = new string[columns.Count];
            for (var i = 0; i < values.Length; i++)
                values[i] = data.IsDBNull(i) ? "NULL" : PgLiteral(data.GetValue(i));
            rows.Add("(" + string.Join(", ", values) + ")");

            if (rows.Count >= InsertBatchSize)
            {
                await writer.WriteLineAsync($"INSERT INTO {PgQuote(schema)}.{PgQuote(table)} ({columnList}) VALUES");
                await writer.WriteLineAsync(string.Join(",\n", rows) + ";");
                rows.Clear();
            }
        }

        if (rows.Count > 0)
        {
            await writer.WriteLineAsync($"INSERT INTO {PgQuote(schema)}.{PgQuote(table)} ({columnList}) VALUES");
            await writer.WriteLineAsync(string.Join(",\n", rows) + ";");
        }
        await writer.FlushAsync();
    }

    // ---------- 保留份数清理 ----------

    private static string Cleanup(string destDir, string triggerName, string ext, int retentionCount)
    {
        if (retentionCount <= 0)
            return string.Empty;

        try
        {
            var prefix = SafeName(triggerName);
            if (!Directory.Exists(destDir))
                return string.Empty;

            var files = Directory.GetFiles(destDir, $"{prefix}-*.{ext}")
                .OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (files.Count <= retentionCount)
                return string.Empty;

            var removed = 0;
            foreach (var old in files.Take(files.Count - retentionCount))
            {
                File.Delete(old);
                removed++;
            }
            return $"已清理 {removed} 份旧备份";
        }
        catch
        {
            return string.Empty;
        }
    }

    // ---------- 纯函数（可单测） ----------

    /// <summary>把用户输入的"指定表"（可能逗号/换行/分号分隔）规范为去重后的表名列表。</summary>
    public static List<string> NormalizeTables(IEnumerable<string>? tables)
    {
        if (tables is null)
            return new List<string>();

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in tables)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            foreach (var part in raw.Split(new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seen.Add(part))
                    result.Add(part);
            }
        }
        return result;
    }

    public static string BuildFileName(string triggerName, string datasourceName, string database, bool gzip)
        => $"{SafeName(triggerName)}-{SafeName(datasourceName)}-{SafeName(database)}-{DateTime.Now:yyyyMMdd-HHmmss}.sql{(gzip ? ".gz" : string.Empty)}";

    public static string MySqlLiteral(object? value)
    {
        switch (value)
        {
            case null:
                return "NULL";
            case bool b:
                return b ? "1" : "0";
            case byte[] bytes:
                return "0x" + Convert.ToHexString(bytes);
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return Convert.ToString(value, CultureInfo.InvariantCulture)!;
            case float f:
                return f.ToString("R", CultureInfo.InvariantCulture);
            case double d:
                return d.ToString("R", CultureInfo.InvariantCulture);
            case decimal m:
                return m.ToString(CultureInfo.InvariantCulture);
            case DateTime dt:
                return "'" + dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "'";
            case DateTimeOffset dto:
                return "'" + dto.ToString("yyyy-MM-dd HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture) + "'";
            case TimeSpan ts:
                return "'" + ts.ToString() + "'";
            default:
                return "'" + EscapeMySqlString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty) + "'";
        }
    }

    public static string PgLiteral(object? value)
    {
        switch (value)
        {
            case null:
                return "NULL";
            case bool b:
                return b ? "true" : "false";
            case byte[] bytes:
                return "'\\x" + Convert.ToHexString(bytes) + "'::bytea";
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return Convert.ToString(value, CultureInfo.InvariantCulture)!;
            case float f:
                return float.IsNaN(f) || float.IsInfinity(f) ? "'" + f.ToString(CultureInfo.InvariantCulture) + "'" : f.ToString("R", CultureInfo.InvariantCulture);
            case double d:
                return double.IsNaN(d) || double.IsInfinity(d) ? "'" + d.ToString(CultureInfo.InvariantCulture) + "'" : d.ToString("R", CultureInfo.InvariantCulture);
            case decimal m:
                return m.ToString(CultureInfo.InvariantCulture);
            case DateTime dt:
                return "'" + dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "'";
            case DateTimeOffset dto:
                return "'" + dto.ToString("yyyy-MM-dd HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture) + "'";
            case TimeSpan ts:
                return "'" + ts.ToString() + "'";
            default:
                return "'" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Replace("'", "''") + "'";
        }
    }

    public static string MySqlQuote(string identifier) => "`" + identifier.Replace("`", "``") + "`";

    public static string PgQuote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string ResolveDatabase(string type, DataSourceConfig ds, string actionDatabase)
    {
        var db = string.IsNullOrWhiteSpace(actionDatabase) ? (ds.DefaultDatabase ?? string.Empty) : actionDatabase.Trim();
        if (type == "postgres" && string.IsNullOrWhiteSpace(db))
            db = "postgres";
        return db;
    }

    private static (string Schema, string Table) SplitPgTable(string qualified)
    {
        var idx = qualified.IndexOf('.');
        return idx > 0
            ? (qualified[..idx], qualified[(idx + 1)..])
            : ("public", qualified);
    }

    private static string MapPgType(string dataType, string udt, int? charLen, int? precision, int? scale)
    {
        switch (dataType.ToLowerInvariant())
        {
            case "character varying":
                return charLen is int vl ? $"varchar({vl.ToString(CultureInfo.InvariantCulture)})" : "varchar";
            case "character":
                return charLen is int cl ? $"char({cl.ToString(CultureInfo.InvariantCulture)})" : "char";
            case "numeric":
                return precision is int p
                    ? $"numeric({p.ToString(CultureInfo.InvariantCulture)}{(scale is int s ? "," + s.ToString(CultureInfo.InvariantCulture) : string.Empty)})"
                    : "numeric";
            case "timestamp without time zone":
                return "timestamp";
            case "timestamp with time zone":
                return "timestamptz";
            case "time without time zone":
                return "time";
            case "time with time zone":
                return "timetz";
            case "user-defined":
                return udt.StartsWith('_') ? udt[1..] + "[]" : udt;
            case "array":
                return udt.StartsWith('_') ? udt[1..] + "[]" : udt;
            default:
                return dataType;
        }
    }

    private static string DescribeScope(IReadOnlyList<string> tables)
        => tables.Count == 0 ? "整库" : $"{tables.Count} 张表";

    private static string EscapeMySqlString(string value)
        => value.Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\0", "\\0")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\u001a", "\\Z");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 忽略 */ }
    }

    private static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "backup";
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { ' ', '\'', '"', '\\', '/' }).ToArray();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim('-');
        return string.IsNullOrEmpty(safe) ? "backup" : safe;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}

/// <summary>
/// 备份并发闸：全局限制同时运行的数据库备份数（容量来自 security.limits.maxConcurrentBackups）。
/// 配置变更时惰性重建信号量（旧信号量待在用等待者释放后由 GC 回收）。
/// </summary>
internal static class BackupConcurrency
{
    private static readonly object Lock = new();
    private static SemaphoreSlim? _semaphore;
    private static int _capacity = 2;

    public static async Task<IDisposable> AcquireAsync(int capacity, CancellationToken ct)
    {
        SemaphoreSlim sem;
        lock (Lock)
        {
            var normalized = Math.Clamp(capacity <= 0 ? 2 : capacity, 1, 16);
            if (_semaphore is null || _capacity != normalized)
            {
                _semaphore = new SemaphoreSlim(normalized, normalized);
                _capacity = normalized;
            }
            sem = _semaphore;
        }

        await sem.WaitAsync(ct);
        return new Releaser(sem);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _semaphore;
        public Releaser(SemaphoreSlim semaphore) => _semaphore = semaphore;
        public void Dispose()
        {
            var sem = Interlocked.Exchange(ref _semaphore, null);
            sem?.Release();
        }
    }
}
