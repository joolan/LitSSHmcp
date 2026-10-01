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
                UNIQUE(FromNode, ToNode, RelationType)
            )";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<TopologyEdge[]> GetEdgesAsync()
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT FromNode, ToNode, RelationType, Evidence, FirstSeenAt, LastSeenAt FROM TopologyEdges";

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
                FirstSeenAt = DateTime.Parse(reader.GetString(4)),
                LastSeenAt = DateTime.Parse(reader.GetString(5))
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
            INSERT INTO TopologyEdges (FromNode, ToNode, RelationType, Evidence, FirstSeenAt, LastSeenAt)
            VALUES (@FromNode, @ToNode, @RelationType, @Evidence, @FirstSeenAt, @LastSeenAt)
            ON CONFLICT(FromNode, ToNode, RelationType)
            DO UPDATE SET Evidence = @Evidence, LastSeenAt = @LastSeenAt";
        cmd.Parameters.AddWithValue("@FromNode", edge.From);
        cmd.Parameters.AddWithValue("@ToNode", edge.To);
        cmd.Parameters.AddWithValue("@RelationType", edge.Type);
        cmd.Parameters.AddWithValue("@Evidence", edge.Evidence ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@FirstSeenAt", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@LastSeenAt", DateTime.UtcNow.ToString("O"));
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
}
