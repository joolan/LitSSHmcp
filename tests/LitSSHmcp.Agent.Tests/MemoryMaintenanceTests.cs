using LitSSHmcp.Agent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

/// <summary>旧数据回填 / 事件蒸馏 / 召回遥测 / 草稿确认 的回归测试。</summary>
public class MemoryMaintenanceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"litssh-maint-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* 忽略 */ }
    }

    private const string Graph = "- ssh:s1 | web01 | 10.0.0.1:22\n- ssh:s2 | db01 | 10.0.0.2:22";

    private async Task CreateSessionMessagesAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS agent_messages(" +
                          "Id INTEGER PRIMARY KEY, SessionId TEXT, Role TEXT, Content TEXT, Timestamp TEXT);";
        await cmd.ExecuteNonQueryAsync();
        cmd.CommandText = "INSERT INTO agent_messages(SessionId, Role, Content) VALUES (@s, 'tool', @c);";
        cmd.Parameters.AddWithValue("@s", "7");
        // 真实会话里 JSON 引号被转义成 \u0022，回填器需先还原再提取 serverId。
        cmd.Parameters.AddWithValue("@c",
            "{\"Tool\":\"ssh_snapshot_get\",\"Args\":\"{\\u0022serverId\\u0022:\\u0022s1\\u0022}\"}");
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<(string Source, string AssetIds, string State)>> ReadRawAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Source, AssetIds, AttributionState FROM agent_memory WHERE Kind='raw' ORDER BY Id;";
        var rows = new List<(string, string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    [Fact]
    public async Task Backfill_uses_session_tool_hints_content_and_is_idempotent()
    {
        var store = new AgentMemoryStore(_dbPath);
        await store.InitializeAsync();
        await CreateSessionMessagesAsync();
        await store.AddAsync("会话:7", "在 web01 上检查 nginx 状态", new[] { 1f, 0f });
        await store.AddAsync("文档:multi.txt#0", "web01 与 db01 的对比记录", new[] { 1f, 0f });
        await store.AddAsync("文档:unknown.txt#0", "随手记的无关片段", new[] { 1f, 0f });

        var first = await MemoryAssetBackfiller.RunAsync(store, Graph);

        Assert.Equal(3, first.Scanned);
        Assert.Equal(1, first.Attributed);   // 会话:7 → ssh:s1
        Assert.Equal(1, first.Multi);        // 内容同时提到两台
        Assert.Equal(1, first.Unknown);
        var rows = await ReadRawAsync(_dbPath);
        Assert.Equal("ssh:s1", rows[0].AssetIds);
        Assert.Equal("attributed", rows[0].State);
        Assert.Equal("multi", rows[1].State);
        Assert.Contains("ssh:s1", rows[1].AssetIds);
        Assert.Contains("ssh:s2", rows[1].AssetIds);
        Assert.Equal("unknown", rows[2].State);
        Assert.Equal(string.Empty, rows[2].AssetIds);

        // 幂等：已判定的不再处理
        var second = await MemoryAssetBackfiller.RunAsync(store, Graph);
        Assert.Equal(0, second.Scanned);
    }

    [Fact]
    public async Task Unknown_attribution_raw_is_downweighted_in_search()
    {
        var store = new AgentMemoryStore(_dbPath);
        await store.InitializeAsync();
        await store.AddAsync("会话:a", "磁盘满 日志 怎么处理", new[] { 1f, 0f });
        await store.AddAsync("会话:b", "磁盘满 日志 怎么处理", new[] { 1f, 0f });
        // 直接按 Id 更新状态（跳过回填器），验证打分降权本身
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE agent_memory SET AttributionState='unknown' WHERE Id=2;";
            await cmd.ExecuteNonQueryAsync();
        }
        var hits = await store.SearchAsync(new[] { 1f, 0f }, 10, "raw", queryText: "磁盘满");
        var known = hits.Single(h => h.Source == "会话:a");
        var unknown = hits.Single(h => h.Source == "会话:b");
        Assert.Equal("unknown", unknown.AttributionState);
        Assert.True(unknown.Score < known.Score,
            $"unknown({unknown.Score:0.000}) 应低于 known({known.Score:0.000})");
    }

    [Fact]
    public async Task Distiller_creates_one_draft_per_incident_and_dedupes()
    {
        var store = new AgentMemoryStore(_dbPath);
        await store.InitializeAsync();
        await store.AddIncidentAsync("磁盘满", "/var 分区 100%", "ssh:s1", "journald 日志膨胀",
            "journalctl --vacuum-size=200M", "df 恢复正常", "df 输出", 0.9, "resolved", "agent-3");

        var first = await ExperienceDistiller.RunAsync(store);
        Assert.Equal(1, first.Created);
        var drafts = await store.ListDraftsAsync(10);
        Assert.Single(drafts);
        Assert.Contains("根因: journald", drafts[0].Content);
        Assert.Contains("处置:", drafts[0].Content);
        Assert.Equal("ssh:s1", drafts[0].AssetIds);

        var second = await ExperienceDistiller.RunAsync(store);
        Assert.Equal(0, second.Created);   // DedupeKey = incident:<id> 去重
    }

    [Fact]
    public async Task Confirm_draft_writes_structured_memory_and_closes_draft()
    {
        var store = new AgentMemoryStore(_dbPath);
        var svc = new AgentMemoryService(store, new ConstEmbedder(), 5, 0.45);
        await svc.InitializeAsync();
        await store.AddIncidentAsync("证书过期", "站点证书过期", "ssh:s1", "未续期",
            "certbot renew 并 reload nginx", "恢复", "openssl 输出", 0.8, "resolved", "agent-4");
        await ExperienceDistiller.RunAsync(store);
        var draft = (await store.ListDraftsAsync(10)).Single();
        var write = MemoryTools.Create(svc)[1];

        var listing = (await write.InvokeAsync(
            new Dictionary<string, object?> { ["action"] = "drafts" }, default))?.ToString() ?? string.Empty;
        Assert.Contains($"#{draft.Id}", listing);

        var result = (await write.InvokeAsync(new Dictionary<string, object?>
        {
            ["action"] = "confirm_draft", ["draftId"] = (int)draft.Id, ["key"] = "cert:renew"
        }, default))?.ToString() ?? string.Empty;
        Assert.Contains("已确认", result);
        Assert.NotNull(await store.FindByKeyAsync("task", "cert:renew"));
        Assert.Empty(await store.ListDraftsAsync(10));
    }

    [Fact]
    public async Task Recall_telemetry_logs_zero_hits_and_merges_outcomes()
    {
        var store = new AgentMemoryStore(_dbPath);
        var svc = new AgentMemoryService(store, new ConstEmbedder(), 5, 0.45);
        await svc.InitializeAsync();

        await svc.RecallAsync("完全无关的查询", sessionId: "42");

        var logs = await store.ListRecentRecallLogsAsync("42", 10);
        Assert.Single(logs);
        Assert.Equal(0, logs[0].HitCount);           // 零命中也记，便于发现阈值过严
        Assert.Contains("structured=", logs[0].Floors);
        Assert.Empty(logs[0].HitKeys);
        await store.MarkRecallOutcomeAsync(logs[0].Id, true, false);
        await store.MarkRecallOutcomeAsync(logs[0].Id, false, true);
        var after = (await store.ListRecentRecallLogsAsync("42", 10)).Single();
        Assert.True(after.Referenced);              // 合并而非覆盖
        Assert.True(after.Corrected);
    }

    private sealed class ConstEmbedder : IEmbeddingGenerator<string, Embedding<float>>, IDisposable
    {
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(new[] { 1f, 0f })).ToList()));
    }
}
