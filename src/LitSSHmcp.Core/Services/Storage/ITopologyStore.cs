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

    /// <summary>删除所有引用"待确认"节点(含 <c>:disc:</c>)的自动发现边。
    /// 每次重新发现前调用，清掉上一轮残留，保证发现结果是"本次"的快照。</summary>
    Task ClearPendingAsync();

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
                UNIQUE(FromNode, ToNode, RelationType)
            )";
        await cmd.ExecuteNonQueryAsync();

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

    public async Task ClearPendingAsync()
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        using var tx = connection.BeginTransaction();
        using (var edges = connection.CreateCommand())
        {
            edges.Transaction = tx;
            edges.CommandText = "DELETE FROM TopologyEdges WHERE FromNode LIKE '%:disc:%' OR ToNode LIKE '%:disc:%'";
            await edges.ExecuteNonQueryAsync();
        }
        using (var nodes = connection.CreateCommand())
        {
            nodes.Transaction = tx;
            nodes.CommandText = "DELETE FROM TopologyNodes WHERE NodeId LIKE '%:disc:%'";
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
}
