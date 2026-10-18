using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LitSSHmcp.Tests;

/// <summary>
/// 审计哈希链跨进程写竞争（评估报告 §4.2 A 级 / §10.A 缺口 4）：
/// 链追加是「读最后 hash → 计算 → 插入」，若只受进程内 _writeGate 保护，
/// 并行会话的多个子进程同时追加会读到同一个 prev → 链分叉 → 校验报「PrevHash 链接不连续」。
/// 每个 AuditLogService 实例持有独立的进程内闸门，因此「多实例并发写同一 db」
/// 与多进程竞争走同一段代码路径（两个连接、两个写闸门、同一个库）。
/// </summary>
public class AuditChainConcurrencyTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "litssh-audit-cq-" + Guid.NewGuid().ToString("N") + ".db");

    private AuditLogService NewService() => new(null, _dbPath);

    private static CommandAuditLog Command(string text) => new()
    {
        ServerId = "s1",
        ServerName = "server-1",
        Command = text,
        Status = CommandStatus.Executed,
        Timestamp = DateTime.UtcNow
    };

    [Fact]
    public async Task Concurrent_writers_keep_chain_intact_and_lose_no_record()
    {
        var init = NewService();
        await init.InitializeAsync();

        const int writers = 6;
        const int perWriter = 6;
        var services = Enumerable.Range(0, writers).Select(_ => NewService()).ToArray();

        var tasks = services.Select((service, index) => Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
                await service.LogCommandAsync(Command($"echo w{index}-{i}"));
        })).ToArray();

        await Task.WhenAll(tasks);

        var verifier = NewService();
        var verify = await verifier.VerifyChainAsync();
        Assert.True(verify.Ok, verify.Message);
        Assert.Equal(writers * perWriter, verify.Checked);

        var logs = await verifier.GetLogsAsync(null, writers * perWriter + 10);
        Assert.Equal(writers * perWriter, logs.Length);
    }

    [Fact]
    public async Task Chain_still_verifies_when_initialize_and_writes_interleave()
    {
        var first = NewService();
        await first.InitializeAsync();

        var initialize = Task.Run(async () =>
        {
            var reinit = NewService();
            await reinit.InitializeAsync();
        });
        var write = Task.Run(async () =>
        {
            var writer = NewService();
            for (var i = 0; i < 5; i++)
                await writer.LogCommandAsync(Command($"echo i-{i}"));
        });

        await Task.WhenAll(initialize, write);

        var verifier = NewService();
        var verify = await verifier.VerifyChainAsync();
        Assert.True(verify.Ok, verify.Message);
        Assert.Equal(5, verify.Checked);
    }

    /// <summary>
    /// 并行会话下另一个写者（子进程/归档搬迁）可能持库写锁较久。
    /// 审计写入必须等待而不是直接 SQLITE_BUSY 失败（旧实现命令超时 3 秒即放弃，审计记录丢失）。
    /// </summary>
    [Fact]
    public async Task Writes_wait_out_a_foreign_writer_holding_the_db_lock()
    {
        var init = NewService();
        await init.InitializeAsync();

        await using var blocker = new SqliteConnection($"Data Source={_dbPath};Default Timeout=30");
        await blocker.OpenAsync();
        await using (var cmd = blocker.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS Blocker(Id INTEGER PRIMARY KEY AUTOINCREMENT);";
            await cmd.ExecuteNonQueryAsync();
        }

        var blockTx = blocker.BeginTransaction();
        await using (var insert = blocker.CreateCommand())
        {
            insert.Transaction = blockTx;
            insert.CommandText = "INSERT INTO Blocker(Id) VALUES(NULL);";
            await insert.ExecuteNonQueryAsync();
        }

        // 锁持有时长超过旧的 3 秒命令超时
        var write = Task.Run(async () =>
        {
            var writer = NewService();
            await writer.LogCommandAsync(Command("echo while-blocked-1"));
            await writer.LogCommandAsync(Command("echo while-blocked-2"));
        });

        await Task.Delay(TimeSpan.FromSeconds(4.5));
        blockTx.Commit();

        await write;

        var verifier = NewService();
        var verify = await verifier.VerifyChainAsync();
        Assert.True(verify.Ok, verify.Message);
        Assert.Equal(2, verify.Checked);

        var logs = await verifier.GetLogsAsync(null, 10);
        Assert.Equal(2, logs.Length);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* ignore */ }
        try { File.Delete(_dbPath + ".key"); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* ignore */ }
        try { File.Delete(_dbPath + ".chain.lock"); } catch { /* ignore */ }
    }
}
