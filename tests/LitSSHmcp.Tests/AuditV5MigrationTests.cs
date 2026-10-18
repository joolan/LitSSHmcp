using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LitSSHmcp.Tests;

/// <summary>
/// 审计格式迁移：v5→v6 保留记录（补 UserId 列 + 重建链），更老版本仍清理重建。
/// </summary>
public class AuditV5MigrationTests
{
    [Fact]
    public async Task V5_migration_preserves_records_and_rebuilds_chain()
    {
        var db = Path.Combine(Path.GetTempPath(), "litssh-audit-mig-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var audit = new AuditLogService(new FakeSecurityOptions(), db);
            await audit.InitializeAsync();
            await audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = "s", ServerName = "n", Command = "ls", UserId = "alice", Status = CommandStatus.Executed
            });
            await audit.LogSqlAsync(new SqlAuditLog
            {
                DataSourceId = "d", DataSourceName = "db", Operation = SqlOperation.Query,
                Sql = "select 1", UserId = "1", Status = CommandStatus.Executed
            });

            // 模拟 v5 库：去掉 UserId 列、版本置 5、链清空（旧链 payload 与 v6 不同）
            using (var conn = new SqliteConnection($"Data Source={db}"))
            {
                conn.Open();
                foreach (var table in new[] { "AuditLogs", "AuditLogsHistory", "SqlAuditLogs", "SqlAuditLogsHistory" })
                {
                    using var alter = conn.CreateCommand();
                    alter.CommandText = $"ALTER TABLE {table} DROP COLUMN UserId;";
                    alter.ExecuteNonQuery();
                }
                using var version = conn.CreateCommand();
                version.CommandText = "PRAGMA user_version = 5;";
                version.ExecuteNonQuery();
                using var clear = conn.CreateCommand();
                clear.CommandText = "DELETE FROM AuditChain;";
                clear.ExecuteNonQuery();
            }

            var upgraded = new AuditLogService(new FakeSecurityOptions(), db);
            await upgraded.InitializeAsync();

            // 记录保留（归因列补回为空 → UI 显示 local），SQL/命令均未丢
            var commands = await upgraded.GetLogsAsync();
            Assert.Single(commands);
            Assert.Equal("ls", commands[0].Command);
            Assert.Null(commands[0].UserId);

            var sql = await upgraded.GetSqlLogsAsync();
            Assert.Single(sql);
            Assert.Equal("select 1", sql[0].Sql);

            // 新链覆盖全部保留记录且校验通过
            var verify = await upgraded.VerifyChainAsync();
            Assert.True(verify.Ok, verify.Message);
            Assert.Equal(2, verify.Checked);

            using (var conn = new SqliteConnection($"Data Source={db}"))
            {
                conn.Open();
                using var version = conn.CreateCommand();
                version.CommandText = "PRAGMA user_version;";
                Assert.Equal(6L, Convert.ToInt64(version.ExecuteScalar()));
                Assert.Contains("UserId", Columns(conn, "AuditLogs"));
                Assert.Contains("UserId", Columns(conn, "SqlAuditLogsHistory"));
            }
        }
        finally
        {
            TryDelete(db);
            TryDelete(db + ".key");
            TryDelete(db + ".chain.lock");
        }
    }

    [Fact]
    public async Task Older_than_v5_is_still_reset()
    {
        var db = Path.Combine(Path.GetTempPath(), "litssh-audit-mig-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var audit = new AuditLogService(new FakeSecurityOptions(), db);
            await audit.InitializeAsync();
            await audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = "s", ServerName = "n", Command = "ls", Status = CommandStatus.Executed
            });

            using (var conn = new SqliteConnection($"Data Source={db}"))
            {
                conn.Open();
                using var version = conn.CreateCommand();
                version.CommandText = "PRAGMA user_version = 4;";
                version.ExecuteNonQuery();
            }

            var upgraded = new AuditLogService(new FakeSecurityOptions(), db);
            await upgraded.InitializeAsync();

            Assert.Empty(await upgraded.GetLogsAsync());
            var verify = await upgraded.VerifyChainAsync();
            Assert.True(verify.Ok, verify.Message);
            Assert.Equal(0, verify.Checked);
        }
        finally
        {
            TryDelete(db);
            TryDelete(db + ".key");
            TryDelete(db + ".chain.lock");
        }
    }

    private static List<string> Columns(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(1));
        return names;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
