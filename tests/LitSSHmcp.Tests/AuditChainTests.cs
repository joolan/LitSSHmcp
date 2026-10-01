using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LitSSHmcp.Tests;

public class AuditChainTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "litssh-audit-" + Guid.NewGuid().ToString("N") + ".db");

    private AuditLogService NewService(ISecurityOptionsProvider? security = null) => new(security, _dbPath);

    private static CommandAuditLog Command(string text, DateTime? at = null) => new()
    {
        ServerId = "s1",
        ServerName = "server-1",
        Command = text,
        Status = CommandStatus.Executed,
        Timestamp = at ?? DateTime.UtcNow
    };

    [Fact]
    public async Task Chain_verifies_after_writes()
    {
        var service = NewService();
        await service.InitializeAsync();

        await service.LogCommandAsync(Command("ls -la"));
        await service.LogSqlAsync(new SqlAuditLog
        {
            DataSourceId = "d1",
            DataSourceName = "订单库",
            Operation = SqlOperation.Query,
            Sql = "select 1",
            Status = CommandStatus.Executed,
            Timestamp = DateTime.UtcNow
        });

        var result = await service.VerifyChainAsync();
        Assert.True(result.Ok, result.Message);
        Assert.Equal(2, result.Checked);
    }

    [Fact]
    public async Task Tamper_is_detected()
    {
        var service = NewService();
        await service.InitializeAsync();
        await service.LogCommandAsync(Command("echo a"));
        await service.LogCommandAsync(Command("echo b"));

        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE AuditLogs SET Command = 'tampered' WHERE Id = 1";
            await cmd.ExecuteNonQueryAsync();
        }

        var result = await service.VerifyChainAsync();
        Assert.False(result.Ok);
        Assert.NotNull(result.FirstBadSeq);
    }

    [Fact]
    public async Task Expired_records_are_archived_to_history_and_chain_stays_valid()
    {
        var security = new FakeSecurityOptions();
        security.Audit.RetentionDays = 1;

        var writer = NewService(security);
        await writer.InitializeAsync();
        await writer.LogCommandAsync(Command("old", DateTime.UtcNow.AddDays(-2)));
        await writer.LogCommandAsync(Command("new", DateTime.UtcNow));

        // 新实例初始化时触发归档（把超期记录移动到历史表）
        var reader = NewService(security);
        await reader.InitializeAsync();

        var live = await reader.GetLogsAsync(null, 100, null, includeHistory: false);
        var all = await reader.GetLogsAsync(null, 100, null, includeHistory: true);

        Assert.Single(live);
        Assert.Equal(2, all.Length);

        var result = await reader.VerifyChainAsync();
        Assert.True(result.Ok, result.Message);
        Assert.Equal(2, result.Checked);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* ignore */ }
        try { File.Delete(_dbPath + ".key"); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* ignore */ }
    }
}
