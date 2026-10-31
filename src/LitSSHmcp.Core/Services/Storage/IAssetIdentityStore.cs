using System.Globalization;
using LitSSHmcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace LitSSHmcp.Core.Services.Storage;

public interface IAssetIdentityStore
{
    Task InitializeAsync();
    Task<bool> TryUpsertAsync(AssetIdentityRecord record);
    Task<IReadOnlyList<AssetIdentityRecord>> GetByAssetAsync(string assetId);
    Task<IReadOnlyList<AssetIdentityRecord>> GetAllAsync();
}

public class AssetIdentityStore : IAssetIdentityStore
{
    private readonly string _dbPath;

    public AssetIdentityStore() : this(null)
    {
    }

    public AssetIdentityStore(string? dbPath)
    {
        _dbPath = string.IsNullOrWhiteSpace(dbPath) ? ConfigPaths.AuditDb : dbPath;
    }

    private string ConnectionString => $"Data Source={_dbPath};Default Timeout=3";

    public async Task InitializeAsync()
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS AssetIdentities (
                AssetId TEXT NOT NULL,
                AssetType TEXT NOT NULL,
                IdentityKind TEXT NOT NULL,
                IdentityValue TEXT NOT NULL,
                Source TEXT NOT NULL,
                Confidence REAL NOT NULL DEFAULT 0.8,
                FirstSeenAt TEXT NOT NULL,
                LastSeenAt TEXT NOT NULL,
                PRIMARY KEY (AssetId, IdentityKind, IdentityValue)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_AssetIdentities_Strong
            ON AssetIdentities(AssetType, IdentityKind, IdentityValue)
            WHERE IdentityKind IN (
                'ssh-machine-id',
                'ssh-host-key',
                'mysql-server-uuid',
                'pg-system-identifier',
                'redis-cluster-id'
            );";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> TryUpsertAsync(AssetIdentityRecord record)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO AssetIdentities (
                AssetId, AssetType, IdentityKind, IdentityValue, Source, Confidence, FirstSeenAt, LastSeenAt)
            VALUES (
                @AssetId, @AssetType, @IdentityKind, @IdentityValue, @Source, @Confidence, @FirstSeenAt, @LastSeenAt)
            ON CONFLICT(AssetId, IdentityKind, IdentityValue)
            DO UPDATE SET
                AssetType = excluded.AssetType,
                Source = excluded.Source,
                Confidence = excluded.Confidence,
                LastSeenAt = excluded.LastSeenAt";
        cmd.Parameters.AddWithValue("@AssetId", record.AssetId);
        cmd.Parameters.AddWithValue("@AssetType", record.AssetType);
        cmd.Parameters.AddWithValue("@IdentityKind", record.IdentityKind);
        cmd.Parameters.AddWithValue("@IdentityValue", record.IdentityValue);
        cmd.Parameters.AddWithValue("@Source", record.Source);
        cmd.Parameters.AddWithValue("@Confidence", record.Confidence);
        cmd.Parameters.AddWithValue("@FirstSeenAt", record.FirstSeenAt.ToString("O"));
        cmd.Parameters.AddWithValue("@LastSeenAt", record.LastSeenAt.ToString("O"));

        try
        {
            await cmd.ExecuteNonQueryAsync();
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<AssetIdentityRecord>> GetByAssetAsync(string assetId)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT AssetId, AssetType, IdentityKind, IdentityValue, Source, Confidence, FirstSeenAt, LastSeenAt
            FROM AssetIdentities
            WHERE AssetId = @AssetId
            ORDER BY IdentityKind, IdentityValue";
        cmd.Parameters.AddWithValue("@AssetId", assetId);
        return await ReadAsync(cmd);
    }

    public async Task<IReadOnlyList<AssetIdentityRecord>> GetAllAsync()
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT AssetId, AssetType, IdentityKind, IdentityValue, Source, Confidence, FirstSeenAt, LastSeenAt
            FROM AssetIdentities
            ORDER BY AssetType, AssetId, IdentityKind, IdentityValue";
        return await ReadAsync(cmd);
    }

    private static async Task<IReadOnlyList<AssetIdentityRecord>> ReadAsync(SqliteCommand command)
    {
        var result = new List<AssetIdentityRecord>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new AssetIdentityRecord
            {
                AssetId = reader.GetString(0),
                AssetType = reader.GetString(1),
                IdentityKind = reader.GetString(2),
                IdentityValue = reader.GetString(3),
                Source = reader.GetString(4),
                Confidence = reader.IsDBNull(5) ? 0.8 : reader.GetDouble(5),
                FirstSeenAt = ReadDateTime(reader, 6) ?? DateTime.UtcNow,
                LastSeenAt = ReadDateTime(reader, 7) ?? DateTime.UtcNow
            });
        }

        return result;
    }

    private static DateTime? ReadDateTime(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        return DateTime.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }
}
