using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.Tests;

public class AuditSessionTests
{
    [Fact]
    public async Task Session_id_is_auto_filled_and_filterable()
    {
        var db = Path.Combine(Path.GetTempPath(), "litssh-audit-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var audit = new AuditLogService(new FakeSecurityOptions(), db);
            await audit.InitializeAsync();
            audit.SessionId = "S1";

            await audit.LogCommandAsync(new CommandAuditLog { ServerId = "s", ServerName = "n", Command = "ls" });
            await audit.LogCommandAsync(new CommandAuditLog { ServerId = "s", ServerName = "n", Command = "pwd", SessionId = "S2" });
            await audit.LogSqlAsync(new SqlAuditLog { DataSourceId = "d", DataSourceName = "db", Operation = SqlOperation.Query, Sql = "select 1" });

            var all = await audit.GetLogsAsync();
            Assert.Equal(2, all.Length);
            Assert.Contains(all, r => r.SessionId == "S1");
            Assert.Contains(all, r => r.SessionId == "S2");

            var s1 = await audit.GetLogsAsync(sessionId: "S1");
            Assert.Single(s1);
            Assert.Equal("ls", s1[0].Command);

            var s2 = await audit.GetLogsAsync(sessionId: "S2");
            Assert.Single(s2);
            Assert.Equal("pwd", s2[0].Command);

            var sql = await audit.GetSqlLogsAsync(sessionId: "S1");
            Assert.Single(sql);
            Assert.Equal("S1", sql[0].SessionId);
        }
        finally
        {
            TryDelete(db);
            TryDelete(db + ".key");
        }
    }

    [Fact]
    public async Task Legacy_db_is_reset_to_current_audit_format()
    {
        var db = Path.Combine(Path.GetTempPath(), "litssh-audit-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            // 构造一个旧格式库（无 SessionId 列、user_version=0）
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE AuditLogs (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT, ServerId TEXT NOT NULL, ServerName TEXT NOT NULL,
                        Command TEXT NOT NULL, Result TEXT, Status TEXT NOT NULL, Timestamp TEXT NOT NULL,
                        ExitCode INTEGER, IsFileTransfer INTEGER NOT NULL DEFAULT 0, FilePath TEXT, FileSize INTEGER);";
                cmd.ExecuteNonQuery();

                cmd.CommandText = "INSERT INTO AuditLogs (ServerId, ServerName, Command, Status, Timestamp) VALUES ('s','n','ls','Executed',@t)";
                cmd.Parameters.AddWithValue("@t", DateTime.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            var audit = new AuditLogService(new FakeSecurityOptions(), db);
            await audit.InitializeAsync(); // 旧格式 → 重建审计数据

            Assert.Empty(await audit.GetLogsAsync()); // 旧记录已按新格式清空

            audit.SessionId = "S9";
            await audit.LogCommandAsync(new CommandAuditLog { ServerId = "s", ServerName = "n", Command = "whoami" });

            var logs = await audit.GetLogsAsync();
            Assert.Single(logs);
            Assert.Equal("S9", logs[0].SessionId);

            Assert.True((await audit.VerifyChainAsync()).Ok);
        }
        finally
        {
            TryDelete(db);
            TryDelete(db + ".key");
        }
    }

    [Fact]
    public async Task Chain_covers_session_id_so_tampering_fails_verification()
    {
        var db = Path.Combine(Path.GetTempPath(), "litssh-audit-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var audit = new AuditLogService(new FakeSecurityOptions(), db);
            await audit.InitializeAsync();
            audit.SessionId = "S1";
            await audit.LogCommandAsync(new CommandAuditLog { ServerId = "s", ServerName = "n", Command = "ls" });

            Assert.True((await audit.VerifyChainAsync()).Ok);

            // 直接篡改会话 ID → 链校验必须失败（会话 ID 已纳入 payload）
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE AuditLogs SET SessionId = 'TAMPERED' WHERE Command = 'ls'";
                cmd.ExecuteNonQuery();
            }

            Assert.False((await audit.VerifyChainAsync()).Ok);
        }
        finally
        {
            TryDelete(db);
            TryDelete(db + ".key");
        }
    }

    [Fact]
    public async Task Sessions_are_recorded_and_upserted()
    {
        var db = Path.Combine(Path.GetTempPath(), "litssh-audit-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var audit = new AuditLogService(new FakeSecurityOptions(), db);
            await audit.InitializeAsync();

            var start = DateTime.UtcNow;
            await audit.RecordSessionAsync(new AuditSession { SessionId = "S1", ClientName = "claude", ClientVersion = "1.0", StartedAt = start, LastSeenAt = start });
            await audit.RecordSessionAsync(new AuditSession { SessionId = "S1", ClientName = "claude", ClientVersion = "1.1", StartedAt = start, LastSeenAt = start.AddSeconds(5) });

            var sessions = await audit.GetSessionsAsync();
            Assert.Single(sessions);
            Assert.Equal("claude", sessions[0].ClientName);
            Assert.Equal("1.1", sessions[0].ClientVersion);

            // 后续客户端信息缺失时不应覆盖已有值（COALESCE）
            await audit.RecordSessionAsync(new AuditSession { SessionId = "S1", ClientName = null, ClientVersion = null, StartedAt = start, LastSeenAt = start.AddSeconds(9) });
            var again = await audit.GetSessionsAsync();
            Assert.Equal("claude", again[0].ClientName);
            Assert.Equal("1.1", again[0].ClientVersion);
        }
        finally
        {
            TryDelete(db);
            TryDelete(db + ".key");
        }
    }

    [Fact]
    public async Task Tool_is_auto_filled_from_context_and_filterable()
    {
        var db = Path.Combine(Path.GetTempPath(), "litssh-audit-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var audit = new AuditLogService(new FakeSecurityOptions(), db);
            await audit.InitializeAsync();
            audit.SessionId = "S1";

            var previous = AuditContext.CurrentTool;
            try
            {
                AuditContext.CurrentTool = "ssh_execute_command";
                await audit.LogCommandAsync(new CommandAuditLog { ServerId = "s", ServerName = "n", Command = "ls" });
                AuditContext.CurrentTool = "docker_logs";
                await audit.LogCommandAsync(new CommandAuditLog { ServerId = "s", ServerName = "n", Command = "docker logs web" });
                await audit.LogSqlAsync(new SqlAuditLog { DataSourceId = "d", DataSourceName = "db", Operation = SqlOperation.Query, Sql = "select 1" });
            }
            finally
            {
                AuditContext.CurrentTool = previous;
            }

            var all = await audit.GetLogsAsync();
            Assert.Contains(all, r => r.Tool == "ssh_execute_command");
            Assert.Contains(all, r => r.Tool == "docker_logs");

            var byTool = await audit.GetLogsAsync(tool: "docker_logs");
            Assert.Single(byTool);
            Assert.Equal("docker logs web", byTool[0].Command);

            var sql = await audit.GetSqlLogsAsync();
            Assert.Equal("docker_logs", sql[0].Tool);
        }
        finally
        {
            TryDelete(db);
            TryDelete(db + ".key");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}