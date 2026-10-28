using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Data.Sqlite;

namespace LitSSHmcp.Agent;

/// <summary>批量变更计划快照：可 JSON 持久化，供 UI 轮询与重启恢复。</summary>
public sealed class FleetPlanState
{
    public string Id { get; set; } = string.Empty;
    public string OwnerKey { get; set; } = "local";
    public long? SessionId { get; set; }
    public List<string> Targets { get; set; } = new();
    public string Command { get; set; } = string.Empty;
    public string RollbackCommand { get; set; } = string.Empty;
    public int Concurrency { get; set; } = 4;
    public int TimeoutSeconds { get; set; } = 60;
    public string RiskNote { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Phase { get; set; } = "planned";
    public bool Running { get; set; }
    public bool DryRunOk { get; set; }
    public bool CanaryOk { get; set; }
    public string? CanaryTarget { get; set; }
    public HashSet<string> AppliedTargets { get; set; } = new(StringComparer.Ordinal);
    public bool RolledBack { get; set; }
    public string? LastError { get; set; }
    public List<FleetTargetState> TargetResults { get; set; } = new();

    [JsonIgnore]
    public object Sync { get; } = new();

    public void UpsertTarget(FleetTargetState result)
    {
        lock (Sync)
        {
            var index = TargetResults.FindIndex(t => string.Equals(t.ServerId, result.ServerId,
                StringComparison.Ordinal));
            if (index >= 0)
            {
                result.ServerName ??= TargetResults[index].ServerName;
                result.Host ??= TargetResults[index].Host;
                TargetResults[index] = result;
            }
            else
            {
                TargetResults.Add(result);
            }
            UpdatedAt = DateTimeOffset.UtcNow;
        }
    }
}

public sealed class FleetTargetState
{
    public string ServerId { get; set; } = string.Empty;
    public string? ServerName { get; set; }
    public string? Host { get; set; }
    public string Phase { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public string? Error { get; set; }
    public string? Output { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public interface IFleetPlanStore
{
    Task SaveAsync(FleetPlanState plan, CancellationToken ct = default);
    Task<FleetPlanState?> GetAsync(string id, string ownerKey, long? sessionId, CancellationToken ct = default);
    Task<List<FleetPlanState>> ListAsync(string ownerKey, long? sessionId, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>删除指定 owner/会话的计划（手动清理）。</summary>
    Task<bool> DeleteScopedAsync(string id, string ownerKey, long? sessionId, CancellationToken ct = default);

    /// <summary>会话删除时清理该会话全部计划。</summary>
    Task DeleteBySessionAsync(long sessionId, CancellationToken ct = default);

    /// <summary>把尚未归属会话的计划迁移到新会话（桌面新建会话首次发送时使用）。</summary>
    Task AdoptSessionAsync(string fromOwnerKey, string toOwnerKey, long sessionId, CancellationToken ct = default);
}

/// <summary>测试/无持久化场景的内存计划库。</summary>
public sealed class MemoryFleetPlanStore : IFleetPlanStore
{
    private readonly ConcurrentDictionary<string, FleetPlanState> _plans = new(StringComparer.Ordinal);

    public Task SaveAsync(FleetPlanState plan, CancellationToken ct = default)
    {
        _plans[plan.Id] = plan;
        return Task.CompletedTask;
    }

    public Task<FleetPlanState?> GetAsync(string id, string ownerKey, long? sessionId,
        CancellationToken ct = default)
    {
        if (!_plans.TryGetValue(id, out var plan))
            return Task.FromResult<FleetPlanState?>(null);
        if (!ScopeMatches(plan, ownerKey, sessionId))
            return Task.FromResult<FleetPlanState?>(null);
        if (DateTimeOffset.UtcNow - plan.CreatedAt > TimeSpan.FromHours(2))
        {
            _plans.TryRemove(id, out _);
            return Task.FromResult<FleetPlanState?>(null);
        }
        NormalizeStale(plan);
        return Task.FromResult<FleetPlanState?>(plan);
    }

    public Task<List<FleetPlanState>> ListAsync(string ownerKey, long? sessionId, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-2);
        var list = _plans.Values
            .Where(p => p.CreatedAt >= cutoff && ScopeMatches(p, ownerKey, sessionId))
            .ToList();
        foreach (var plan in list) NormalizeStale(plan);
        return Task.FromResult(list.OrderByDescending(p => p.UpdatedAt).ToList());
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        _plans.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteScopedAsync(string id, string ownerKey, long? sessionId,
        CancellationToken ct = default)
    {
        if (!_plans.TryGetValue(id, out var plan) || !ScopeMatches(plan, ownerKey, sessionId))
            return Task.FromResult(false);
        return Task.FromResult(_plans.TryRemove(id, out _));
    }

    public Task DeleteBySessionAsync(long sessionId, CancellationToken ct = default)
    {
        foreach (var pair in _plans)
        {
            if (pair.Value.SessionId == sessionId)
                _plans.TryRemove(pair.Key, out _);
        }
        return Task.CompletedTask;
    }

    public Task AdoptSessionAsync(string fromOwnerKey, string toOwnerKey, long sessionId,
        CancellationToken ct = default)
    {
        foreach (var plan in _plans.Values)
        {
            if (plan.SessionId is not null ||
                !string.Equals(plan.OwnerKey, fromOwnerKey, StringComparison.Ordinal))
                continue;
            plan.OwnerKey = toOwnerKey;
            plan.SessionId = sessionId;
            plan.UpdatedAt = DateTimeOffset.UtcNow;
        }
        return Task.CompletedTask;
    }

    internal static bool ScopeMatches(FleetPlanState plan, string ownerKey, long? sessionId) =>
        string.Equals(plan.OwnerKey, ownerKey, StringComparison.Ordinal) &&
        (sessionId is null || plan.SessionId == sessionId);

    /// <summary>进程崩溃/取消后遗留的 Running 计划：超过 15 分钟无进度则标记为 stale。</summary>
    internal static bool NormalizeStale(FleetPlanState plan)
    {
        if (!plan.Running || DateTimeOffset.UtcNow - plan.UpdatedAt <= TimeSpan.FromMinutes(15))
            return false;
        plan.Running = false;
        plan.Phase = "stale";
        plan.LastError = "执行进程已中断或超时，请检查目标状态后重新 plan/rollback";
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }
}

/// <summary>SQLite 计划库：与 agent.db 同库不同表，支持桌面/Web 重启后恢复。</summary>
public sealed class SqliteFleetPlanStore : IFleetPlanStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _dbPath;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteFleetPlanStore(string? dbPath = null) =>
        _dbPath = string.IsNullOrWhiteSpace(dbPath) ? ConfigPaths.AgentDb : dbPath!;

    private string ConnectionString => $"Data Source={_dbPath};Default Timeout=3";

    private static bool NormalizeStale(FleetPlanState plan) => MemoryFleetPlanStore.NormalizeStale(plan);

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(ct);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS agent_fleet_plans (
                    Id TEXT PRIMARY KEY,
                    OwnerKey TEXT NOT NULL,
                    SessionId INTEGER NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    StateJson TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_agent_fleet_plans_owner
                    ON agent_fleet_plans(OwnerKey, SessionId, UpdatedAt);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task SaveAsync(FleetPlanState plan, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        string json;
        lock (plan.Sync) json = JsonSerializer.Serialize(plan, JsonOptions);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_fleet_plans(Id, OwnerKey, SessionId, CreatedAt, UpdatedAt, StateJson)
            VALUES($id, $owner, $session, $created, $updated, $json)
            ON CONFLICT(Id) DO UPDATE SET
                OwnerKey=excluded.OwnerKey,
                SessionId=excluded.SessionId,
                UpdatedAt=excluded.UpdatedAt,
                StateJson=excluded.StateJson;
            """;
        cmd.Parameters.AddWithValue("$id", plan.Id);
        cmd.Parameters.AddWithValue("$owner", plan.OwnerKey);
        cmd.Parameters.AddWithValue("$session", (object?)plan.SessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", plan.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", plan.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$json", json);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<FleetPlanState?> GetAsync(string id, string ownerKey, long? sessionId,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT StateJson FROM agent_fleet_plans WHERE Id=$id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id);
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrWhiteSpace(json))
            return null;
        FleetPlanState? plan;
        try
        {
            plan = JsonSerializer.Deserialize<FleetPlanState>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
        if (plan is null || !MemoryFleetPlanStore.ScopeMatches(plan, ownerKey, sessionId))
            return null;
        if (DateTimeOffset.UtcNow - plan.CreatedAt > TimeSpan.FromHours(2))
        {
            await DeleteAsync(id, ct);
            return null;
        }
        if (NormalizeStale(plan))
            await SaveAsync(plan, ct);
        return plan;
    }

    public async Task<List<FleetPlanState>> ListAsync(string ownerKey, long? sessionId,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sessionId is null
            ? "SELECT StateJson FROM agent_fleet_plans WHERE OwnerKey=$owner AND CreatedAt>=$cutoff ORDER BY UpdatedAt DESC LIMIT 50;"
            : "SELECT StateJson FROM agent_fleet_plans WHERE OwnerKey=$owner AND SessionId=$session AND CreatedAt>=$cutoff ORDER BY UpdatedAt DESC LIMIT 50;";
        cmd.Parameters.AddWithValue("$owner", ownerKey);
        cmd.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddHours(-2).ToString("O"));
        if (sessionId is not null)
            cmd.Parameters.AddWithValue("$session", sessionId.Value);
        var list = new List<FleetPlanState>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            try
            {
                var plan = JsonSerializer.Deserialize<FleetPlanState>(reader.GetString(0), JsonOptions);
                if (plan is not null)
                {
                    NormalizeStale(plan);
                    list.Add(plan);
                }
            }
            catch
            {
                // 跳过损坏记录，不影响其他计划
            }
        }
        return list;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_fleet_plans WHERE Id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteScopedAsync(string id, string ownerKey, long? sessionId,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sessionId is null
            ? "DELETE FROM agent_fleet_plans WHERE Id=$id AND OwnerKey=$owner AND SessionId IS NULL;"
            : "DELETE FROM agent_fleet_plans WHERE Id=$id AND OwnerKey=$owner AND SessionId=$session;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$owner", ownerKey);
        if (sessionId is not null)
            cmd.Parameters.AddWithValue("$session", sessionId.Value);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task DeleteBySessionAsync(long sessionId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_fleet_plans WHERE SessionId=$session;";
        cmd.Parameters.AddWithValue("$session", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AdoptSessionAsync(string fromOwnerKey, string toOwnerKey, long sessionId,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        var rows = new List<(string Id, string Json)>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT Id, StateJson FROM agent_fleet_plans
                WHERE OwnerKey=$fromOwner AND SessionId IS NULL;
                """;
            select.Parameters.AddWithValue("$fromOwner", fromOwnerKey);
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var row in rows)
        {
            FleetPlanState? plan;
            try
            {
                plan = JsonSerializer.Deserialize<FleetPlanState>(row.Json, JsonOptions);
            }
            catch
            {
                continue;
            }
            if (plan is null) continue;
            plan.OwnerKey = toOwnerKey;
            plan.SessionId = sessionId;
            plan.UpdatedAt = DateTimeOffset.UtcNow;
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE agent_fleet_plans
                SET OwnerKey=$toOwner, SessionId=$session, UpdatedAt=$updated, StateJson=$json
                WHERE Id=$id;
                """;
            update.Parameters.AddWithValue("$toOwner", toOwnerKey);
            update.Parameters.AddWithValue("$session", sessionId);
            update.Parameters.AddWithValue("$updated", plan.UpdatedAt.ToString("O"));
            update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(plan, JsonOptions));
            update.Parameters.AddWithValue("$id", row.Id);
            await update.ExecuteNonQueryAsync(ct);
        }
    }
}
