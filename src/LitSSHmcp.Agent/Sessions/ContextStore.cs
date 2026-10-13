using LitSSHmcp.Core.Services.Storage;
using Microsoft.Data.Sqlite;

namespace LitSSHmcp.Agent;

/// <summary>AI 助手会话与消息的持久化（独立库 agent.db）。</summary>
public interface IContextStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<long> CreateSessionAsync(string title, CancellationToken ct = default);
    Task<List<AgentSessionRow>> ListSessionsAsync(CancellationToken ct = default);
    Task RenameSessionAsync(long id, string title, CancellationToken ct = default);
    Task DeleteSessionAsync(long id, CancellationToken ct = default);

    /// <summary>归档指定会话（不再出现在会话列表，可在“归档记录”中恢复或删除）。</summary>
    Task ArchiveSessionAsync(long id, CancellationToken ct = default);

    /// <summary>列出已归档的会话（按更新时间倒序）。</summary>
    Task<List<AgentSessionRow>> ListArchivedSessionsAsync(CancellationToken ct = default);

    /// <summary>把已归档会话恢复到会话列表。</summary>
    Task RestoreSessionAsync(long id, CancellationToken ct = default);

    /// <summary>记录该会话最近一次使用的模型 Id（切换会话时优先用它）。</summary>
    Task SetSessionProviderAsync(long id, string providerId, CancellationToken ct = default);

    Task AppendMessageAsync(long sessionId, string role, string content, CancellationToken ct = default);

    /// <summary>用给定的完整消息序列替换该会话的持久化记录（编辑/重发/重生成后保持与界面一致）。</summary>
    Task ReplaceMessagesAsync(long sessionId, IEnumerable<(string Role, string Content)> messages, CancellationToken ct = default);

    Task<List<AgentMessageRow>> GetMessagesAsync(long sessionId, CancellationToken ct = default);
    Task TouchSessionAsync(long id, CancellationToken ct = default);

    /// <summary>读取会话的滚动摘要（裁剪旧轮次后压缩保存的文本；无则空串）。</summary>
    Task<string> GetSessionSummaryAsync(long id, CancellationToken ct = default);

    /// <summary>保存（或清空传空串）会话的滚动摘要。</summary>
    Task SetSessionSummaryAsync(long id, string summary, CancellationToken ct = default);

    /// <summary>读取会话的任务计划（JSON 数组；无则空串）。</summary>
    Task<string> GetSessionPlanAsync(long id, CancellationToken ct = default);

    /// <summary>保存（或清空传空串）会话的任务计划。</summary>
    Task SetSessionPlanAsync(long id, string planJson, CancellationToken ct = default);

    /// <summary>读取会话激活的技能 Id（JSON 数组；无则空串=索引模式）。</summary>
    Task<string> GetSessionActiveSkillsAsync(long id, CancellationToken ct = default);

    /// <summary>保存（或清空传空串）会话激活的技能 Id。</summary>
    Task SetSessionActiveSkillsAsync(long id, string activeSkillsJson, CancellationToken ct = default);

    /// <summary>读取会话记忆的智能体 Id（无则空串=回退全局默认）。</summary>
    Task<string> GetSessionAgentIdAsync(long id, CancellationToken ct = default);

    /// <summary>保存会话记忆的智能体 Id。</summary>
    Task SetSessionAgentIdAsync(long id, string agentId, CancellationToken ct = default);

    /// <summary>读取会话是否处于计划模式。</summary>
    Task<bool> GetSessionPlanModeAsync(long id, CancellationToken ct = default);

    /// <summary>保存会话计划模式。</summary>
    Task SetSessionPlanModeAsync(long id, bool planMode, CancellationToken ct = default);

    /// <summary>读取会话的工具审批策略（tool→allow/deny 的 JSON；无则空串）。</summary>
    Task<string> GetSessionToolPolicyAsync(long id, CancellationToken ct = default);

    /// <summary>保存会话的工具审批策略。</summary>
    Task SetSessionToolPolicyAsync(long id, string toolPolicyJson, CancellationToken ct = default);

    /// <summary>设置会话所属分组（空串=未分组）。</summary>
    Task SetSessionGroupAsync(long id, string group, CancellationToken ct = default);

    /// <summary>列出已定义的分组名（按名称排序）。</summary>
    Task<List<string>> ListGroupsAsync(CancellationToken ct = default);

    /// <summary>新增分组（已存在则忽略）。</summary>
    Task AddGroupAsync(string name, CancellationToken ct = default);

    /// <summary>重命名分组（同步更新其下会话的分组名）。</summary>
    Task RenameGroupAsync(string oldName, string newName, CancellationToken ct = default);

    /// <summary>删除分组（其下会话归入「未分组」）。</summary>
    Task DeleteGroupAsync(string name, CancellationToken ct = default);

    /// <summary>把会话移动到指定分组并设置排序值（值越小越靠前）。</summary>
    Task MoveSessionAsync(long id, string group, double sortOrder, CancellationToken ct = default);

    /// <summary>设置分组排序值（值越小越靠前）。</summary>
    Task MoveGroupAsync(string name, double sortOrder, CancellationToken ct = default);

    /// <summary>按给定顺序重排分组（写入 0,1,2… 的排序值）。</summary>
    Task ReorderGroupsAsync(IReadOnlyList<string> orderedNames, CancellationToken ct = default);

    /// <summary>按保留策略裁剪历史（每会话消息上限 / 最旧会话天数 / 会话数上限）；返回删除的消息数。</summary>
    Task<int> PruneAsync(int maxSessions, int maxMessagesPerSession, int retentionDays, CancellationToken ct = default);
}

public sealed record AgentSessionRow(long Id, string Title, string CreatedAt, string UpdatedAt, string? ProviderId = null, string Group = "", double SortOrder = 0)
{
    /// <summary>会话最后活跃的相对时间（刚刚 / x分钟前 / x小时前 / x天前 / 日期）。</summary>
    public string UpdatedText => RelativeTime.Format(UpdatedAt);

    /// <summary>分组显示名（空=未分组）。</summary>
    public string GroupText => string.IsNullOrWhiteSpace(Group) ? "未分组" : Group;
}

/// <summary>相对时间格式化（本地时区）。</summary>
public static class RelativeTime
{
    public static string Format(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso) ||
            !DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var when))
            return string.Empty;

        var local = when.ToLocalTime();
        var delta = DateTime.Now - local;
        if (delta.TotalSeconds < 0)
            return "刚刚";
        if (delta.TotalMinutes < 1)
            return "刚刚";
        if (delta.TotalMinutes < 60)
            return $"{(int)delta.TotalMinutes}分钟前";
        if (delta.TotalHours < 24)
            return $"{(int)delta.TotalHours}小时前";
        if (delta.TotalDays < 30)
            return $"{(int)delta.TotalDays}天前";
        return local.ToString("yyyy-MM-dd");
    }
}
public sealed record AgentMessageRow(string Role, string Content, string Timestamp);

public class ContextStore : IContextStore
{
    private readonly string _dbPath;

    public ContextStore() : this(null) { }
    public ContextStore(string? dbPath) => _dbPath = string.IsNullOrWhiteSpace(dbPath) ? ConfigPaths.AgentDb : dbPath!;

    private string ConnectionString => $"Data Source={_dbPath};Default Timeout=3";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS agent_sessions (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Title TEXT NOT NULL DEFAULT '',
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS agent_messages (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SessionId INTEGER NOT NULL,
    Role TEXT NOT NULL,
    Content TEXT NOT NULL,
    Timestamp TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_agent_messages_SessionId ON agent_messages(SessionId, Id);";
        await cmd.ExecuteNonQueryAsync(ct);

        // 兼容旧库：补 ProviderId 列（已存在则忽略）
        try
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE agent_sessions ADD COLUMN ProviderId TEXT;";
            await alter.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // 列已存在
        }

        // 兼容旧库：补 Archived 列（0=正常，1=已归档）
        try
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE agent_sessions ADD COLUMN Archived INTEGER NOT NULL DEFAULT 0;";
            await alter.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // 列已存在
        }

        // 兼容旧库：补 Summary 列（滚动摘要，跨重启恢复用）
        try
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE agent_sessions ADD COLUMN Summary TEXT NOT NULL DEFAULT '';";
            await alter.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // 列已存在
        }

        // 兼容旧库：补 Plan 列（任务计划 JSON，跨重启恢复用）
        try
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE agent_sessions ADD COLUMN Plan TEXT NOT NULL DEFAULT '';";
            await alter.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // 列已存在
        }

        // 兼容旧库：补 ActiveSkills 列（会话激活技能 Id 的 JSON 数组）
        try
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE agent_sessions ADD COLUMN ActiveSkills TEXT NOT NULL DEFAULT '';";
            await alter.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // 列已存在
        }

        // 兼容旧库：补 AgentId 列（会话记忆的智能体 Id；''=回退全局默认）
        try
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE agent_sessions ADD COLUMN AgentId TEXT NOT NULL DEFAULT '';";
            await alter.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // 列已存在
        }

        // 兼容旧库：补 PlanMode 列（0=执行模式，1=计划模式）
        try
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE agent_sessions ADD COLUMN PlanMode INTEGER NOT NULL DEFAULT 0;";
            await alter.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // 列已存在
        }

        // 兼容旧库：补 ToolPolicy 列（工具审批策略 JSON）
        try
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE agent_sessions ADD COLUMN ToolPolicy TEXT NOT NULL DEFAULT '';";
            await alter.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // 列已存在
        }

        // 兼容旧库：补 Group 列（会话分组名；''=未分组）
        try
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE agent_sessions ADD COLUMN \"Group\" TEXT NOT NULL DEFAULT '';";
            await alter.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // 列已存在
        }

        // 分组定义表（用于持久化分组名，使"空分组"也能显示）
        try
        {
            await using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE IF NOT EXISTS agent_groups (Name TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, SortOrder REAL NOT NULL DEFAULT 0);";
            await create.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // 忽略
        }

        // 兼容旧库：补 SortOrder 列（会话排序；值越小越靠前）
        try
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE agent_sessions ADD COLUMN SortOrder REAL NOT NULL DEFAULT 0;";
            await alter.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // 列已存在
        }

        // 兼容旧库：补分组 SortOrder 列
        try
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE agent_groups ADD COLUMN SortOrder REAL NOT NULL DEFAULT 0;";
            await alter.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // 列已存在
        }
    }

    public async Task<long> CreateSessionAsync(string title, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        var now = DateTime.UtcNow.ToString("O");
        // 新会话排到最前（SortOrder 取当前最小值 - 1）
        cmd.CommandText = "INSERT INTO agent_sessions(Title, CreatedAt, UpdatedAt, SortOrder) " +
                          "VALUES (@t, @now, @now, (SELECT COALESCE(MIN(SortOrder), 1) - 1 FROM agent_sessions)); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@t", title ?? string.Empty);
        cmd.Parameters.AddWithValue("@now", now);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<List<AgentSessionRow>> ListSessionsAsync(CancellationToken ct = default)
    {
        var list = new List<AgentSessionRow>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Title, CreatedAt, UpdatedAt, ProviderId, \"Group\", SortOrder FROM agent_sessions WHERE Archived=0 ORDER BY SortOrder ASC, UpdatedAt DESC;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new AgentSessionRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                reader.IsDBNull(6) ? 0 : reader.GetDouble(6)));
        return list;
    }

    public async Task<List<AgentSessionRow>> ListArchivedSessionsAsync(CancellationToken ct = default)
    {
        var list = new List<AgentSessionRow>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Title, CreatedAt, UpdatedAt, ProviderId, \"Group\", SortOrder FROM agent_sessions WHERE Archived=1 ORDER BY UpdatedAt DESC;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new AgentSessionRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                reader.IsDBNull(6) ? 0 : reader.GetDouble(6)));
        return list;
    }

    public Task RenameSessionAsync(long id, string title, CancellationToken ct = default) =>
        ExecAsync("UPDATE agent_sessions SET Title=@v, UpdatedAt=@now WHERE Id=@id;",
            new() { ["@v"] = title, ["@now"] = DateTime.UtcNow.ToString("O"), ["@id"] = id }, ct);

    public Task SetSessionProviderAsync(long id, string providerId, CancellationToken ct = default) =>
        ExecAsync("UPDATE agent_sessions SET ProviderId=@v WHERE Id=@id;",
            new() { ["@v"] = providerId ?? string.Empty, ["@id"] = id }, ct);

    public Task DeleteSessionAsync(long id, CancellationToken ct = default) =>
        ExecAsync("DELETE FROM agent_messages WHERE SessionId=@id; DELETE FROM agent_sessions WHERE Id=@id;",
            new() { ["@id"] = id }, ct);

    public Task ArchiveSessionAsync(long id, CancellationToken ct = default) =>
        ExecAsync("UPDATE agent_sessions SET Archived=1 WHERE Id=@id;", new() { ["@id"] = id }, ct);

    public Task RestoreSessionAsync(long id, CancellationToken ct = default) =>
        ExecAsync("UPDATE agent_sessions SET Archived=0, UpdatedAt=@now WHERE Id=@id;",
            new() { ["@now"] = DateTime.UtcNow.ToString("O"), ["@id"] = id }, ct);

    public Task TouchSessionAsync(long id, CancellationToken ct = default) =>
        ExecAsync("UPDATE agent_sessions SET UpdatedAt=@now WHERE Id=@id;",
            new() { ["@now"] = DateTime.UtcNow.ToString("O"), ["@id"] = id }, ct);

    public async Task<string> GetSessionSummaryAsync(long id, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Summary FROM agent_sessions WHERE Id=@id;";
        cmd.Parameters.AddWithValue("@id", id);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is string s ? s : string.Empty;
    }

    public Task SetSessionSummaryAsync(long id, string summary, CancellationToken ct = default) =>
        ExecAsync("UPDATE agent_sessions SET Summary=@v WHERE Id=@id;",
            new() { ["@v"] = summary ?? string.Empty, ["@id"] = id }, ct);

    public async Task<string> GetSessionPlanAsync(long id, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Plan FROM agent_sessions WHERE Id=@id;";
        cmd.Parameters.AddWithValue("@id", id);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is string s ? s : string.Empty;
    }

    public Task SetSessionPlanAsync(long id, string planJson, CancellationToken ct = default) =>
        ExecAsync("UPDATE agent_sessions SET Plan=@v WHERE Id=@id;",
            new() { ["@v"] = planJson ?? string.Empty, ["@id"] = id }, ct);

    public async Task<string> GetSessionActiveSkillsAsync(long id, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ActiveSkills FROM agent_sessions WHERE Id=@id;";
        cmd.Parameters.AddWithValue("@id", id);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is string s ? s : string.Empty;
    }

    public Task SetSessionActiveSkillsAsync(long id, string activeSkillsJson, CancellationToken ct = default) =>
        ExecAsync("UPDATE agent_sessions SET ActiveSkills=@v WHERE Id=@id;",
            new() { ["@v"] = activeSkillsJson ?? string.Empty, ["@id"] = id }, ct);

    public async Task<string> GetSessionAgentIdAsync(long id, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT AgentId FROM agent_sessions WHERE Id=@id;";
        cmd.Parameters.AddWithValue("@id", id);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is string s ? s : string.Empty;
    }

    public Task SetSessionAgentIdAsync(long id, string agentId, CancellationToken ct = default) =>
        ExecAsync("UPDATE agent_sessions SET AgentId=@v WHERE Id=@id;",
            new() { ["@v"] = agentId ?? string.Empty, ["@id"] = id }, ct);

    public async Task<bool> GetSessionPlanModeAsync(long id, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT PlanMode FROM agent_sessions WHERE Id=@id;";
        cmd.Parameters.AddWithValue("@id", id);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long value && value != 0;
    }

    public Task SetSessionPlanModeAsync(long id, bool planMode, CancellationToken ct = default) =>
        ExecAsync("UPDATE agent_sessions SET PlanMode=@v WHERE Id=@id;",
            new() { ["@v"] = planMode ? 1 : 0, ["@id"] = id }, ct);

    public async Task<string> GetSessionToolPolicyAsync(long id, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ToolPolicy FROM agent_sessions WHERE Id=@id;";
        cmd.Parameters.AddWithValue("@id", id);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is string s ? s : string.Empty;
    }

    public Task SetSessionToolPolicyAsync(long id, string toolPolicyJson, CancellationToken ct = default) =>
        ExecAsync("UPDATE agent_sessions SET ToolPolicy=@v WHERE Id=@id;",
            new() { ["@v"] = toolPolicyJson ?? string.Empty, ["@id"] = id }, ct);

    public Task SetSessionGroupAsync(long id, string group, CancellationToken ct = default) =>
        ExecAsync("UPDATE agent_sessions SET \"Group\"=@v WHERE Id=@id;",
            new() { ["@v"] = group ?? string.Empty, ["@id"] = id }, ct);

    public async Task<List<string>> ListGroupsAsync(CancellationToken ct = default)
    {
        var list = new List<string>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Name FROM agent_groups ORDER BY SortOrder ASC, Name COLLATE NOCASE;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(reader.GetString(0));
        return list;
    }

    public Task AddGroupAsync(string name, CancellationToken ct = default) =>
        ExecAsync("INSERT OR IGNORE INTO agent_groups(Name, CreatedAt, SortOrder) " +
                  "VALUES (@n, @now, (SELECT COALESCE(MAX(SortOrder), 0) + 1 FROM agent_groups));",
            new() { ["@n"] = (name ?? string.Empty).Trim(), ["@now"] = DateTime.UtcNow.ToString("O") }, ct);

    public async Task RenameGroupAsync(string oldName, string newName, CancellationToken ct = default)
    {
        var from = (oldName ?? string.Empty).Trim();
        var to = (newName ?? string.Empty).Trim();
        if (from.Length == 0 || to.Length == 0 || string.Equals(from, to, StringComparison.Ordinal))
            return;

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE agent_sessions SET \"Group\"=@to WHERE \"Group\"=@from;" +
                              "INSERT OR IGNORE INTO agent_groups(Name, CreatedAt, SortOrder) VALUES (@to, @now, (SELECT COALESCE(SortOrder, 0) FROM agent_groups WHERE Name=@from));" +
                              "DELETE FROM agent_groups WHERE Name=@from;";
            cmd.Parameters.AddWithValue("@to", to);
            cmd.Parameters.AddWithValue("@from", from);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task DeleteGroupAsync(string name, CancellationToken ct = default)
    {
        var group = (name ?? string.Empty).Trim();
        if (group.Length == 0)
            return;

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE agent_sessions SET \"Group\"='' WHERE \"Group\"=@g;" +
                              "DELETE FROM agent_groups WHERE Name=@g;";
            cmd.Parameters.AddWithValue("@g", group);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public Task MoveSessionAsync(long id, string group, double sortOrder, CancellationToken ct = default) =>
        ExecAsync("UPDATE agent_sessions SET \"Group\"=@g, SortOrder=@o WHERE Id=@id;",
            new() { ["@g"] = group ?? string.Empty, ["@o"] = sortOrder, ["@id"] = id }, ct);

    public Task MoveGroupAsync(string name, double sortOrder, CancellationToken ct = default) =>
        ExecAsync("UPDATE agent_groups SET SortOrder=@o WHERE Name=@n;",
            new() { ["@o"] = sortOrder, ["@n"] = name ?? string.Empty }, ct);

    public async Task ReorderGroupsAsync(IReadOnlyList<string> orderedNames, CancellationToken ct = default)
    {
        if (orderedNames is null || orderedNames.Count == 0)
            return;

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE agent_groups SET SortOrder=@o WHERE Name=@n;";
            var pn = cmd.Parameters.Add("@n", SqliteType.Text);
            var po = cmd.Parameters.Add("@o", SqliteType.Real);
            for (var i = 0; i < orderedNames.Count; i++)
            {
                pn.Value = orderedNames[i];
                po.Value = (double)i;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
    }

    public Task AppendMessageAsync(long sessionId, string role, string content, CancellationToken ct = default) =>
        ExecAsync("INSERT INTO agent_messages(SessionId, Role, Content, Timestamp) VALUES (@s, @r, @c, @now);",
            new() { ["@s"] = sessionId, ["@r"] = role, ["@c"] = content, ["@now"] = DateTime.UtcNow.ToString("O") }, ct);

    public async Task ReplaceMessagesAsync(long sessionId, IEnumerable<(string Role, string Content)> messages, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var del = connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM agent_messages WHERE SessionId=@s;";
            del.Parameters.AddWithValue("@s", sessionId);
            await del.ExecuteNonQueryAsync(ct);
        }

        foreach (var (role, content) in messages)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO agent_messages(SessionId, Role, Content, Timestamp) VALUES (@s, @r, @c, @now);";
            insert.Parameters.AddWithValue("@s", sessionId);
            insert.Parameters.AddWithValue("@r", role);
            insert.Parameters.AddWithValue("@c", content);
            insert.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<List<AgentMessageRow>> GetMessagesAsync(long sessionId, CancellationToken ct = default)
    {
        var list = new List<AgentMessageRow>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Role, Content, Timestamp FROM agent_messages WHERE SessionId=@s ORDER BY Id;";
        cmd.Parameters.AddWithValue("@s", sessionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new AgentMessageRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return list;
    }

    public async Task<int> PruneAsync(int maxSessions, int maxMessagesPerSession, int retentionDays, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);

        async Task<int> RunAsync(string sql, Dictionary<string, object> args)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in args)
                cmd.Parameters.AddWithValue(k, v);
            return await cmd.ExecuteNonQueryAsync(ct);
        }

        var deleted = 0;

        // 1) 每会话消息上限（保留最新 N 条）
        if (maxMessagesPerSession > 0)
        {
            deleted += await RunAsync(
                @"DELETE FROM agent_messages WHERE Id IN (
                    SELECT Id FROM (SELECT Id, ROW_NUMBER() OVER (PARTITION BY SessionId ORDER BY Id DESC) AS rn FROM agent_messages)
                    WHERE rn > @n);",
                new() { ["@n"] = maxMessagesPerSession });
        }

        // 2) 过期会话（按天数）及其消息（已归档会话受保护，不参与裁剪）
        if (retentionDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays).ToString("O");
            await RunAsync("DELETE FROM agent_messages WHERE SessionId IN (SELECT Id FROM agent_sessions WHERE UpdatedAt < @cutoff AND Archived=0);",
                new() { ["@cutoff"] = cutoff });
            await RunAsync("DELETE FROM agent_sessions WHERE UpdatedAt < @cutoff AND Archived=0;", new() { ["@cutoff"] = cutoff });
        }

        // 3) 会话数上限（保留最新 N 个；已归档会话受保护）
        if (maxSessions > 0)
        {
            await RunAsync(
                @"DELETE FROM agent_messages WHERE SessionId IN (SELECT Id FROM agent_sessions WHERE Archived=0)
                    AND SessionId NOT IN (SELECT Id FROM (SELECT Id FROM agent_sessions WHERE Archived=0 ORDER BY UpdatedAt DESC LIMIT @n));",
                new() { ["@n"] = maxSessions });
            await RunAsync(
                @"DELETE FROM agent_sessions WHERE Archived=0
                    AND Id NOT IN (SELECT Id FROM (SELECT Id FROM agent_sessions WHERE Archived=0 ORDER BY UpdatedAt DESC LIMIT @n));",
                new() { ["@n"] = maxSessions });
        }

        return deleted;
    }

    private async Task ExecAsync(string sql, Dictionary<string, object> args, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
