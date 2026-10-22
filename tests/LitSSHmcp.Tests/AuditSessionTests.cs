using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.Tests;

public class AuditSessionTests
{
    [Fact]
    public async Task Audit_command_and_sql_redact_secrets_before_persistence_and_chain_stays_valid()
    {
        var db = Path.Combine(Path.GetTempPath(), "litssh-audit-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var audit = new AuditLogService(new FakeSecurityOptions(), db);
            await audit.InitializeAsync();

            await audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = "s",
                ServerName = "n",
                Command = "curl --password hunter2 https://user:pass@example",
                Result = "token=abcdef123456",
                Status = CommandStatus.Executed
            });
            await audit.LogSqlAsync(new SqlAuditLog
            {
                DataSourceId = "d",
                DataSourceName = "db",
                Operation = SqlOperation.Query,
                Sql = "select * from users where password='hunter2'",
                Result = "Authorization: Bearer abcdefghijklmnop",
                Status = CommandStatus.Executed
            });

            var commands = await audit.GetLogsAsync();
            var command = Assert.Single(commands);
            Assert.DoesNotContain("hunter2", command.Command, StringComparison.Ordinal);
            Assert.DoesNotContain("abcdef123456", command.Result!, StringComparison.Ordinal);
            Assert.Contains("******", command.Command, StringComparison.Ordinal);
            Assert.Contains("[敏感内容已自动脱敏]", command.Result!, StringComparison.Ordinal);

            var sqls = await audit.GetSqlLogsAsync();
            var sql = Assert.Single(sqls);
            Assert.DoesNotContain("hunter2", sql.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("abcdefghijklmnop", sql.Result!, StringComparison.Ordinal);
            Assert.Contains("[敏感内容已自动脱敏]", sql.Result!, StringComparison.Ordinal);

            Assert.True((await audit.VerifyChainAsync()).Ok);
        }
        finally
        {
            TryDelete(db);
            TryDelete(db + ".key");
        }
    }

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
    public async Task Legacy_db_is_upgraded_and_records_are_preserved()
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
            await audit.InitializeAsync(); // 旧格式 → 补齐缺失列并保留记录、重建链

            var legacy = await audit.GetLogsAsync();
            var old = Assert.Single(legacy);
            Assert.Equal("ls", old.Command);
            Assert.Null(old.SessionId); // 旧记录缺失的会话列保持为空

            audit.SessionId = "S9";
            await audit.LogCommandAsync(new CommandAuditLog { ServerId = "s", ServerName = "n", Command = "whoami" });

            var logs = await audit.GetLogsAsync();
            Assert.Equal(2, logs.Length);
            Assert.Contains(logs, l => l.Command == "ls");
            Assert.Contains(logs, l => l.Command == "whoami" && l.SessionId == "S9");

            var verify = await audit.VerifyChainAsync();
            Assert.True(verify.Ok, verify.Message);
            Assert.Equal(2, verify.Checked);
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
            await audit.RecordSessionAsync(new AuditSession { SessionId = "S1", ClientName = "claude", ClientVersion = "1.0", OwnerUserId = "alice", StartedAt = start, LastSeenAt = start });
            await audit.RecordSessionAsync(new AuditSession { SessionId = "S1", ClientName = "claude", ClientVersion = "1.1", StartedAt = start, LastSeenAt = start.AddSeconds(5) });

            var sessions = await audit.GetSessionsAsync();
            Assert.Single(sessions);
            Assert.Equal("claude", sessions[0].ClientName);
            Assert.Equal("1.1", sessions[0].ClientVersion);
            Assert.Equal("alice", sessions[0].OwnerUserId);

            // 后续客户端信息/归属缺失时不应覆盖已有值（COALESCE）
            await audit.RecordSessionAsync(new AuditSession { SessionId = "S1", ClientName = null, ClientVersion = null, StartedAt = start, LastSeenAt = start.AddSeconds(9) });
            var again = await audit.GetSessionsAsync();
            Assert.Equal("claude", again[0].ClientName);
            Assert.Equal("1.1", again[0].ClientVersion);
            Assert.Equal("alice", again[0].OwnerUserId);
        }
        finally
        {
            TryDelete(db);
            TryDelete(db + ".key");
        }
    }

    [Fact]
    public async Task Sessions_are_filtered_by_owner()
    {
        var db = Path.Combine(Path.GetTempPath(), "litssh-audit-" + Guid.NewGuid().ToString("N") + ".db");
        var previousUser = AuditContext.CurrentUserId;
        try
        {
            var audit = new AuditLogService(new FakeSecurityOptions(), db);
            await audit.InitializeAsync();
            var start = DateTime.UtcNow;

            AuditContext.CurrentUserId = "alice";
            await audit.RecordSessionAsync(new AuditSession { SessionId = "S1", StartedAt = start, LastSeenAt = start });
            AuditContext.CurrentUserId = null;
            await audit.RecordSessionAsync(new AuditSession { SessionId = "S2", OwnerUserId = "bob", StartedAt = start, LastSeenAt = start });
            await audit.RecordSessionAsync(new AuditSession { SessionId = "S3", StartedAt = start, LastSeenAt = start });

            var all = await audit.GetSessionsAsync();
            Assert.Equal(3, all.Length);

            var alice = await audit.GetSessionsAsync(ownerUserId: "alice");
            Assert.Equal("S1", Assert.Single(alice).SessionId);
            Assert.Equal("alice", alice[0].OwnerUserId);

            var bob = await audit.GetSessionsAsync(ownerUserId: "bob");
            Assert.Equal("S2", Assert.Single(bob).SessionId);

            // 未显式归属且无上下文的桌面会话按 local 归因
            var local = await audit.GetSessionsAsync(ownerUserId: "local");
            Assert.Equal("S3", Assert.Single(local).SessionId);

            Assert.Empty(await audit.GetSessionsAsync(ownerUserId: "nobody"));
        }
        finally
        {
            AuditContext.CurrentUserId = previousUser;
            TryDelete(db);
            TryDelete(db + ".key");
        }
    }

    [Fact]
    public async Task Legacy_sessions_table_gains_owner_column_and_backfills_on_next_record()
    {
        var db = Path.Combine(Path.GetTempPath(), "litssh-audit-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE Sessions (
                        SessionId TEXT PRIMARY KEY,
                        ClientName TEXT,
                        ClientVersion TEXT,
                        StartedAt TEXT NOT NULL,
                        LastSeenAt TEXT NOT NULL);";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "INSERT INTO Sessions (SessionId, StartedAt, LastSeenAt) VALUES ('S0', @t, @t)";
                cmd.Parameters.AddWithValue("@t", DateTime.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            var audit = new AuditLogService(new FakeSecurityOptions(), db);
            await audit.InitializeAsync();

            var migrated = Assert.Single(await audit.GetSessionsAsync());
            Assert.Null(migrated.OwnerUserId);

            await audit.RecordSessionAsync(new AuditSession
            {
                SessionId = "S0", OwnerUserId = "alice", StartedAt = migrated.StartedAt, LastSeenAt = DateTime.UtcNow
            });

            var backfilled = Assert.Single(await audit.GetSessionsAsync(ownerUserId: "alice"));
            Assert.Equal("S0", backfilled.SessionId);
            Assert.Equal("alice", backfilled.OwnerUserId);
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
                AuditContext.CurrentTool = "docker_query";
                await audit.LogCommandAsync(new CommandAuditLog { ServerId = "s", ServerName = "n", Command = "docker logs web" });
                await audit.LogSqlAsync(new SqlAuditLog { DataSourceId = "d", DataSourceName = "db", Operation = SqlOperation.Query, Sql = "select 1" });
            }
            finally
            {
                AuditContext.CurrentTool = previous;
            }

            var all = await audit.GetLogsAsync();
            Assert.Contains(all, r => r.Tool == "ssh_execute_command");
            Assert.Contains(all, r => r.Tool == "docker_query");

            var byTool = await audit.GetLogsAsync(tool: "docker_query");
            Assert.Single(byTool);
            Assert.Equal("docker logs web", byTool[0].Command);

            var sql = await audit.GetSqlLogsAsync();
            Assert.Equal("docker_query", sql[0].Tool);
        }
        finally
        {
            TryDelete(db);
            TryDelete(db + ".key");
        }
    }

    [Fact]
    public async Task Keyword_matches_user_id_or_command()
    {
        var db = Path.Combine(Path.GetTempPath(), "litssh-audit-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var audit = new AuditLogService(new FakeSecurityOptions(), db);
            await audit.InitializeAsync();
            audit.SessionId = "S1";

            await audit.LogCommandAsync(new CommandAuditLog { ServerId = "s", ServerName = "n", Command = "ls", UserId = "alice" });
            await audit.LogCommandAsync(new CommandAuditLog { ServerId = "s", ServerName = "n", Command = "pwd", UserId = "1" });
            await audit.LogSqlAsync(new SqlAuditLog
            {
                DataSourceId = "d", DataSourceName = "db", Operation = SqlOperation.Query, Sql = "select 1", UserId = "alice"
            });

            // 关键字命中用户归因（Web 用户 Id / 桌面登录用户名）与命令文本
            var byUser = await audit.GetLogsAsync(keyword: "alice");
            Assert.Single(byUser);
            Assert.Equal("ls", byUser[0].Command);

            var byCommand = await audit.GetLogsAsync(keyword: "pwd");
            Assert.Single(byCommand);
            Assert.Equal("1", byCommand[0].UserId);

            var sqlByUser = await audit.GetSqlLogsAsync(keyword: "alice");
            Assert.Single(sqlByUser);
            Assert.Equal("select 1", sqlByUser[0].Sql);

            // 用户名搜索 → UI 先把用户名映射为用户 Id，再按 Id 集合过滤
            var byIds = await audit.GetLogsAsync(userIds: new[] { "1" });
            Assert.Single(byIds);
            Assert.Equal("pwd", byIds[0].Command);

            var sqlByIds = await audit.GetSqlLogsAsync(userIds: new[] { "alice" });
            Assert.Single(sqlByIds);
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