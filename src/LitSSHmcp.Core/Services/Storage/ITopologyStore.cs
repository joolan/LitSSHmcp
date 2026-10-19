using System.Globalization;
using LitSSHmcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace LitSSHmcp.Core.Services.Storage;

public interface ITopologyStore
{
    Task InitializeAsync();
    Task<TopologyEdge[]> GetEdgesAsync();
    Task UpsertEdgeAsync(TopologyEdge edge);

    /// <summary>删除引用指定节点(作为起点或终点)的所有自动发现边(用于删除资产后清理)。</summary>
    Task RemoveEdgesByNodeAsync(string nodeId);

    /// <summary>删除一条指定的自动发现边。</summary>
    Task RemoveEdgeAsync(string from, string to, string type);

    /// <summary>把自动发现边中引用 <paramref name="oldNodeId"/> 的端点替换为 <paramref name="newNodeId"/>
    /// （用于把"待确认"节点确认为已登记资产后，重定向其关系）。</summary>
    Task ReplaceNodeAsync(string oldNodeId, string newNodeId);

    /// <summary>清空全部“自动发现”边与节点信息。每次重新发现前调用，保证发现结果是“本次”的完整快照
    /// （拓扑库只存自动发现结果，人工关系在 config.Relations，不受影响）。</summary>
    Task ClearDiscoveredAsync();

    /// <summary>写入/更新自动发现节点的附加信息（JSON，如监听端口）。</summary>
    Task UpsertNodeInfoAsync(string nodeId, string infoJson);

    /// <summary>读取所有自动发现节点的附加信息（nodeId → JSON）。</summary>
    Task<Dictionary<string, string>> GetNodeInfosAsync();
}

public class TopologyStore : ITopologyStore
{
    private readonly string _dbPath;

    public TopologyStore()
    {
        _dbPath = ConfigPaths.AuditDb;
    }

    private string ConnectionString => $"Data Source={_dbPath};Default Timeout=3";

    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS TopologyEdges (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FromNode TEXT NOT NULL,
                ToNode TEXT NOT NULL,
                RelationType TEXT NOT NULL,
                Evidence TEXT,
                FirstSeenAt TEXT NOT NULL,
                LastSeenAt TEXT NOT NULL,
                Confidence REAL NOT NULL DEFAULT 0.8,
                Disputed INTEGER NOT NULL DEFAULT 0,
                DisputeReason TEXT,
                DisputeAcknowledged INTEGER NOT NULL DEFAULT 0,
                ValidFrom TEXT,
                ValidTo TEXT,
                LastVerifiedAt TEXT,
                UNIQUE(FromNode, ToNode, RelationType)
            )";
        await cmd.ExecuteNonQueryAsync();

        await EnsureColumnAsync(connection, "TopologyEdges", "Confidence", "REAL NOT NULL DEFAULT 0.8");
        await EnsureColumnAsync(connection, "TopologyEdges", "Disputed", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "TopologyEdges", "DisputeReason", "TEXT");
        await EnsureColumnAsync(connection, "TopologyEdges", "DisputeAcknowledged", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "TopologyEdges", "ValidFrom", "TEXT");
        await EnsureColumnAsync(connection, "TopologyEdges", "ValidTo", "TEXT");
        await EnsureColumnAsync(connection, "TopologyEdges", "LastVerifiedAt", "TEXT");

        using var index = connection.CreateCommand();
        index.CommandText = @"
            CREATE INDEX IF NOT EXISTS IX_TopologyEdges_From ON TopologyEdges(FromNode, RelationType);
            CREATE INDEX IF NOT EXISTS IX_TopologyEdges_To ON TopologyEdges(ToNode, RelationType);";
        await index.ExecuteNonQueryAsync();

        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS TopologyNodes (
                NodeId TEXT PRIMARY KEY,
                Info TEXT NOT NULL
            )";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<TopologyEdge[]> GetEdgesAsync()
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT FromNode, ToNode, RelationType, Evidence, FirstSeenAt, LastSeenAt, " +
            "Confidence, Disputed, ValidFrom, ValidTo, LastVerifiedAt, DisputeReason, DisputeAcknowledged " +
            "FROM TopologyEdges";

        var edges = new List<TopologyEdge>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            edges.Add(new TopologyEdge
            {
                From = reader.GetString(0),
                To = reader.GetString(1),
                Type = reader.GetString(2),
                Evidence = reader.IsDBNull(3) ? null : reader.GetString(3),
                Source = "discovered",
                FirstSeenAt = ReadDateTime(reader, 4) ?? DateTime.UtcNow,
                LastSeenAt = ReadDateTime(reader, 5) ?? DateTime.UtcNow,
                Confidence = reader.IsDBNull(6) ? 0.8 : reader.GetDouble(6),
                Disputed = !reader.IsDBNull(7) && reader.GetInt64(7) != 0,
                ValidFrom = ReadDateTime(reader, 8),
                ValidTo = ReadDateTime(reader, 9),
                LastVerifiedAt = ReadDateTime(reader, 10),
                DisputeReason = reader.IsDBNull(11) ? null : reader.GetString(11),
                DisputeAcknowledged = !reader.IsDBNull(12) && reader.GetInt64(12) != 0
            });
        }

        return edges.ToArray();
    }

    public async Task UpsertEdgeAsync(TopologyEdge edge)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO TopologyEdges (
                FromNode, ToNode, RelationType, Evidence, FirstSeenAt, LastSeenAt,
                Confidence, Disputed, DisputeReason, DisputeAcknowledged, ValidFrom, ValidTo, LastVerifiedAt)
            VALUES (
                @FromNode, @ToNode, @RelationType, @Evidence, @FirstSeenAt, @LastSeenAt,
                @Confidence, @Disputed, @DisputeReason, @DisputeAcknowledged, @ValidFrom, @ValidTo, @LastVerifiedAt)
            ON CONFLICT(FromNode, ToNode, RelationType)
            DO UPDATE SET
                Evidence = @Evidence,
                LastSeenAt = @LastSeenAt,
                Confidence = @Confidence,
                Disputed = @Disputed,
                DisputeReason = @DisputeReason,
                DisputeAcknowledged = @DisputeAcknowledged,
                ValidFrom = @ValidFrom,
                ValidTo = @ValidTo,
                LastVerifiedAt = @LastVerifiedAt";
        cmd.Parameters.AddWithValue("@FromNode", edge.From);
        cmd.Parameters.AddWithValue("@ToNode", edge.To);
        cmd.Parameters.AddWithValue("@RelationType", edge.Type);
        cmd.Parameters.AddWithValue("@Evidence", edge.Evidence ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FirstSeenAt", edge.FirstSeenAt.ToString("O"));
        cmd.Parameters.AddWithValue("@LastSeenAt", edge.LastSeenAt.ToString("O"));
        cmd.Parameters.AddWithValue("@Confidence", edge.Confidence);
        cmd.Parameters.AddWithValue("@Disputed", edge.Disputed ? 1 : 0);
        cmd.Parameters.AddWithValue("@DisputeReason", edge.DisputeReason ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@DisputeAcknowledged", edge.DisputeAcknowledged ? 1 : 0);
        cmd.Parameters.AddWithValue("@ValidFrom", edge.ValidFrom?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ValidTo", edge.ValidTo?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@LastVerifiedAt", edge.LastVerifiedAt?.ToString("O") ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RemoveEdgesByNodeAsync(string nodeId)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM TopologyEdges WHERE FromNode = @Node OR ToNode = @Node";
        cmd.Parameters.AddWithValue("@Node", nodeId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RemoveEdgeAsync(string from, string to, string type)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM TopologyEdges WHERE FromNode = @From AND ToNode = @To AND RelationType = @Type";
        cmd.Parameters.AddWithValue("@From", from);
        cmd.Parameters.AddWithValue("@To", to);
        cmd.Parameters.AddWithValue("@Type", type);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ReplaceNodeAsync(string oldNodeId, string newNodeId)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        using var tx = connection.BeginTransaction();
        // UPDATE OR IGNORE：若替换后与已有边唯一键冲突，则跳过（保留已存在的新边），随后删掉残留的旧边
        foreach (var (col, other) in new[] { ("FromNode", "ToNode"), ("ToNode", "FromNode") })
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                $"UPDATE OR IGNORE TopologyEdges SET {col} = @New WHERE {col} = @Old;" +
                $"DELETE FROM TopologyEdges WHERE {col} = @Old;";
            cmd.Parameters.AddWithValue("@New", newNodeId);
            cmd.Parameters.AddWithValue("@Old", oldNodeId);
            await cmd.ExecuteNonQueryAsync();
        }

        using (var nodeCmd = connection.CreateCommand())
        {
            nodeCmd.Transaction = tx;
            nodeCmd.CommandText =
                "UPDATE OR IGNORE TopologyNodes SET NodeId = @New WHERE NodeId = @Old;" +
                "DELETE FROM TopologyNodes WHERE NodeId = @Old;";
            nodeCmd.Parameters.AddWithValue("@New", newNodeId);
            nodeCmd.Parameters.AddWithValue("@Old", oldNodeId);
            await nodeCmd.ExecuteNonQueryAsync();
        }

        tx.Commit();
    }

    public async Task ClearDiscoveredAsync()
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        using var tx = connection.BeginTransaction();
        using (var edges = connection.CreateCommand())
        {
            edges.Transaction = tx;
            edges.CommandText = "DELETE FROM TopologyEdges";
            await edges.ExecuteNonQueryAsync();
        }
        using (var nodes = connection.CreateCommand())
        {
            nodes.Transaction = tx;
            nodes.CommandText = "DELETE FROM TopologyNodes";
            await nodes.ExecuteNonQueryAsync();
        }
        tx.Commit();
    }

    public async Task UpsertNodeInfoAsync(string nodeId, string infoJson)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO TopologyNodes (NodeId, Info) VALUES (@Id, @Info)
            ON CONFLICT(NodeId) DO UPDATE SET Info = @Info";
        cmd.Parameters.AddWithValue("@Id", nodeId);
        cmd.Parameters.AddWithValue("@Info", infoJson);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<Dictionary<string, string>> GetNodeInfosAsync()
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT NodeId, Info FROM TopologyNodes";

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[reader.GetString(0)] = reader.GetString(1);

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

    private static async Task EnsureColumnAsync(
        SqliteConnection connection, string table, string column, string definition)
    {
        using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table})";
            await using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        try
        {
            await alter.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // 另一个进程已完成同一迁移。
        }
    }
}
