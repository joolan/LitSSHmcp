using LitSSHmcp.McpServer.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

public class SqliteJobStoreEventColumnsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"litssh-jobs-migrate-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Old_event_table_is_migrated_and_structured_fields_are_persisted()
    {
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE mcp_job_events (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    JobId TEXT NOT NULL,
                    Seq INTEGER NOT NULL,
                    Kind TEXT NOT NULL,
                    Message TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var store = new SqliteJobStore(_dbPath);
        await store.AppendEventAsync("job_1", JobEventKinds.Progress,
            """{"server":"web-1","phase":"done","done":1,"total":2,"status":"running"}""");

        var events = await store.GetEventsAsync("job_1", 0, 10);
        var evt = Assert.Single(events);
        Assert.Equal("web-1", evt.Server);
        Assert.Equal("done", evt.Phase);
        Assert.Equal(1, evt.Done);
        Assert.Equal(2, evt.Total);
        Assert.Equal("running", evt.Status);
    }

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}
