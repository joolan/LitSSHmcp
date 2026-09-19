using LitSSHmcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace LitSSHmcp.Core.Services.Storage;

public interface IAuditLogService
{
    Task InitializeAsync();
    Task LogCommandAsync(CommandAuditLog log);
    Task<CommandAuditLog[]> GetLogsAsync(string? serverId = null, int limit = 100);
}

public class AuditLogService : IAuditLogService
{
    private readonly string _dbPath;

    public AuditLogService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "LitSSH");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "audit.db");
    }

    private string ConnectionString => $"Data Source={_dbPath}";

    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS AuditLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ServerId TEXT NOT NULL,
                ServerName TEXT NOT NULL,
                Command TEXT NOT NULL,
                Result TEXT,
                Status TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                ExitCode INTEGER,
                IsFileTransfer INTEGER NOT NULL DEFAULT 0,
                FilePath TEXT,
                FileSize INTEGER
            )";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task LogCommandAsync(CommandAuditLog log)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO AuditLogs (ServerId, ServerName, Command, Result, Status, Timestamp, ExitCode, IsFileTransfer, FilePath, FileSize)
            VALUES (@ServerId, @ServerName, @Command, @Result, @Status, @Timestamp, @ExitCode, @IsFileTransfer, @FilePath, @FileSize)";

        cmd.Parameters.AddWithValue("@ServerId", log.ServerId);
        cmd.Parameters.AddWithValue("@ServerName", log.ServerName);
        cmd.Parameters.AddWithValue("@Command", log.Command);
        cmd.Parameters.AddWithValue("@Result", log.Result ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@Status", log.Status.ToString());
        cmd.Parameters.AddWithValue("@Timestamp", log.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("@ExitCode", log.ExitCode ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@IsFileTransfer", log.IsFileTransfer ? 1 : 0);
        cmd.Parameters.AddWithValue("@FilePath", log.FilePath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FileSize", log.FileSize ?? (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<CommandAuditLog[]> GetLogsAsync(string? serverId = null, int limit = 100)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        var cmd = connection.CreateCommand();
        if (serverId != null)
        {
            cmd.CommandText = "SELECT * FROM AuditLogs WHERE ServerId = @ServerId ORDER BY Timestamp DESC LIMIT @Limit";
            cmd.Parameters.AddWithValue("@ServerId", serverId);
        }
        else
        {
            cmd.CommandText = "SELECT * FROM AuditLogs ORDER BY Timestamp DESC LIMIT @Limit";
        }
        cmd.Parameters.AddWithValue("@Limit", limit);

        var logs = new List<CommandAuditLog>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            logs.Add(new CommandAuditLog
            {
                Id = reader.GetInt64(0),
                ServerId = reader.GetString(1),
                ServerName = reader.GetString(2),
                Command = reader.GetString(3),
                Result = reader.IsDBNull(4) ? null : reader.GetString(4),
                Status = Enum.Parse<CommandStatus>(reader.GetString(5)),
                Timestamp = DateTime.Parse(reader.GetString(6)),
                ExitCode = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                IsFileTransfer = reader.GetInt32(8) == 1,
                FilePath = reader.IsDBNull(9) ? null : reader.GetString(9),
                FileSize = reader.IsDBNull(10) ? null : reader.GetInt64(10)
            });
        }

        return logs.ToArray();
    }
}