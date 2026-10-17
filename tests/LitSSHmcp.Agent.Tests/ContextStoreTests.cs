using System.Linq;
using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class ContextStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "litssh-agent-" + Guid.NewGuid().ToString("N") + ".db");
    private ContextStore NewStore() => new(_dbPath);

    [Fact]
    public async Task Session_and_messages_roundtrip()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var id = await store.CreateSessionAsync("会话1");
        await store.AppendMessageAsync(id, "user", "你好");
        await store.AppendMessageAsync(id, "assistant", "在的");

        var sessions = await store.ListSessionsAsync();
        Assert.Single(sessions);
        Assert.Equal("会话1", sessions[0].Title);

        var messages = await store.GetMessagesAsync(id);
        Assert.Equal(2, messages.Count);
        Assert.Equal("user", messages[0].Role);
        Assert.Equal("在的", messages[1].Content);
    }

    [Fact]
    public async Task Session_remembers_last_used_provider()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.CreateSessionAsync("会话");
        Assert.Null((await store.ListSessionsAsync())[0].ProviderId);

        await store.SetSessionProviderAsync(id, "model-xyz");
        Assert.Equal("model-xyz", (await store.ListSessionsAsync())[0].ProviderId);
    }

    [Fact]
    public async Task Session_remembers_last_used_agent()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.CreateSessionAsync("会话");
        Assert.Equal(string.Empty, await store.GetSessionAgentIdAsync(id));

        await store.SetSessionAgentIdAsync(id, "ops");
        Assert.Equal("ops", await store.GetSessionAgentIdAsync(id));

        await store.SetSessionAgentIdAsync(id, string.Empty);
        Assert.Equal(string.Empty, await store.GetSessionAgentIdAsync(id));
    }

    [Fact]
    public async Task Groups_add_assign_rename_delete()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.CreateSessionAsync("会话");

        await store.AddGroupAsync("生产");
        Assert.Contains("生产", await store.ListGroupsAsync());

        await store.SetSessionGroupAsync(id, "生产");
        Assert.Equal("生产", (await store.ListSessionsAsync())[0].Group);
        Assert.Equal("生产", (await store.ListSessionsAsync())[0].GroupText);

        await store.RenameGroupAsync("生产", "线上");
        Assert.Contains("线上", await store.ListGroupsAsync());
        Assert.DoesNotContain("生产", await store.ListGroupsAsync());
        Assert.Equal("线上", (await store.ListSessionsAsync())[0].Group);

        await store.DeleteGroupAsync("线上");
        Assert.Empty(await store.ListGroupsAsync());
        Assert.Equal(string.Empty, (await store.ListSessionsAsync())[0].Group);
        Assert.Equal("未分组", (await store.ListSessionsAsync())[0].GroupText);
    }

    [Fact]
    public async Task Rename_and_delete()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.CreateSessionAsync("旧");
        await store.AppendMessageAsync(id, "user", "x");

        await store.RenameSessionAsync(id, "新");
        Assert.Equal("新", (await store.ListSessionsAsync())[0].Title);

        await store.DeleteSessionAsync(id);
        Assert.Empty(await store.ListSessionsAsync());
        Assert.Empty(await store.GetMessagesAsync(id));
    }

    [Fact]
    public async Task Sessions_are_ordered_and_movable()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var a = await store.CreateSessionAsync("a");
        var b = await store.CreateSessionAsync("b");
        var c = await store.CreateSessionAsync("c");

        // 新会话排到最前 → 顺序为 c, b, a
        var sessions = await store.ListSessionsAsync();
        Assert.Equal(new[] { c, b, a }, sessions.Select(s => s.Id));

        // 把 a 移到最前
        await store.MoveSessionAsync(a, string.Empty, sessions[0].SortOrder - 1);
        Assert.Equal(a, (await store.ListSessionsAsync())[0].Id);

        // 移动到分组并置顶
        await store.AddGroupAsync("生产");
        await store.MoveSessionAsync(b, "生产", 0);
        var grouped = (await store.ListSessionsAsync()).First(s => s.Id == b);
        Assert.Equal("生产", grouped.Group);
    }

    [Fact]
    public async Task Groups_reorder_persists()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.AddGroupAsync("G1");
        await store.AddGroupAsync("G2");
        await store.AddGroupAsync("G3");
        Assert.Equal(new[] { "G1", "G2", "G3" }, await store.ListGroupsAsync());

        await store.ReorderGroupsAsync(new[] { "G3", "G1", "G2" });
        Assert.Equal(new[] { "G3", "G1", "G2" }, await store.ListGroupsAsync());
    }

    [Fact]
    public async Task ReplaceMessages_replaces_full_transcript()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.CreateSessionAsync("s");
        await store.AppendMessageAsync(id, "user", "旧");
        await store.AppendMessageAsync(id, "assistant", "旧答");

        await store.ReplaceMessagesAsync(id, new[]
        {
            ("user", "新"),
            ("assistant", "新答")
        });

        var messages = await store.GetMessagesAsync(id);
        Assert.Equal(2, messages.Count);
        Assert.Equal("新", messages[0].Content);
        Assert.Equal("新答", messages[1].Content);
    }

    [Fact]
    public async Task Prune_caps_messages_and_sessions()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var a = await store.CreateSessionAsync("a");
        for (var i = 0; i < 5; i++)
            await store.AppendMessageAsync(a, "user", "m" + i);
        await store.PruneAsync(maxSessions: 0, maxMessagesPerSession: 2, retentionDays: 0);
        Assert.Equal(2, (await store.GetMessagesAsync(a)).Count);

        await Task.Delay(10);
        var b = await store.CreateSessionAsync("b");
        await Task.Delay(10);
        var c = await store.CreateSessionAsync("c");
        await store.PruneAsync(maxSessions: 2, maxMessagesPerSession: 0, retentionDays: 0);

        var sessions = await store.ListSessionsAsync();
        Assert.Equal(2, sessions.Count);
        Assert.DoesNotContain(sessions, s => s.Id == a);   // 最旧的会话被裁掉
        Assert.Contains(sessions, s => s.Id == c);
    }

    [Fact]
    public async Task Archive_hides_from_list_and_restore_brings_back()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.CreateSessionAsync("归档我");
        await store.AppendMessageAsync(id, "user", "hi");

        await store.ArchiveSessionAsync(id);
        Assert.Empty(await store.ListSessionsAsync());
        var archived = await store.ListArchivedSessionsAsync();
        Assert.Single(archived);
        Assert.Equal(id, archived[0].Id);

        await store.RestoreSessionAsync(id);
        Assert.Single(await store.ListSessionsAsync());
        Assert.Empty(await store.ListArchivedSessionsAsync());
        Assert.Single(await store.GetMessagesAsync(id));
    }

    [Fact]
    public async Task Archived_sessions_survive_prune()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var kept = await store.CreateSessionAsync("归档保留");
        await store.ArchiveSessionAsync(kept);

        await Task.Delay(10);
        await store.CreateSessionAsync("a");
        await Task.Delay(10);
        await store.CreateSessionAsync("b");

        await store.PruneAsync(maxSessions: 1, maxMessagesPerSession: 0, retentionDays: 0);

        Assert.Single(await store.ListArchivedSessionsAsync());
        Assert.Equal(kept, (await store.ListArchivedSessionsAsync())[0].Id);
        Assert.Single(await store.ListSessionsAsync());
    }

    [Fact]
    public async Task Delete_archived_removes_it()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.CreateSessionAsync("s");
        await store.ArchiveSessionAsync(id);

        await store.DeleteSessionAsync(id);
        Assert.Empty(await store.ListArchivedSessionsAsync());
        Assert.Empty(await store.GetMessagesAsync(id));
    }

    [Fact]
    public async Task Session_plan_roundtrip()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.CreateSessionAsync("s");
        Assert.Equal(string.Empty, await store.GetSessionPlanAsync(id));

        await store.SetSessionPlanAsync(id, "[{\"Text\":\"排查\",\"Done\":true}]");
        Assert.Equal("[{\"Text\":\"排查\",\"Done\":true}]", await store.GetSessionPlanAsync(id));

        await store.SetSessionPlanAsync(id, string.Empty);
        Assert.Equal(string.Empty, await store.GetSessionPlanAsync(id));
    }

    [Fact]
    public async Task Session_workspace_dir_and_owner_roundtrip()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.CreateSessionAsync("s");

        // v5 默认值：工作目录未分配（''），归属 local（桌面未登录，评估报告 D1）
        Assert.Equal(string.Empty, await store.GetSessionWorkspaceDirAsync(id));
        Assert.Equal("local", await store.GetSessionOwnerUserIdAsync(id));

        await store.SetSessionWorkspaceDirAsync(id, @"C:\LitSSH\jobs\web-user-2\abc123");
        await store.SetSessionOwnerUserIdAsync(id, "web-user-2");
        Assert.Equal(@"C:\LitSSH\jobs\web-user-2\abc123", await store.GetSessionWorkspaceDirAsync(id));
        Assert.Equal("web-user-2", await store.GetSessionOwnerUserIdAsync(id));

        // 空白归属回退 local（防脏数据）
        await store.SetSessionOwnerUserIdAsync(id, "   ");
        Assert.Equal("local", await store.GetSessionOwnerUserIdAsync(id));
    }

    [Fact]
    public async Task Session_enabled_mcp_subset_roundtrip()
    {
        // D8 启用层：会话级连接器子集（v6 列 EnabledMcp；''=继承全局默认集合）
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.CreateSessionAsync("会话");

        Assert.Equal(string.Empty, await store.GetSessionEnabledMcpAsync(id));

        await store.SetSessionEnabledMcpAsync(id, @"[""litssh"",""ext1""]");
        Assert.Equal(@"[""litssh"",""ext1""]", await store.GetSessionEnabledMcpAsync(id));

        await store.SetSessionEnabledMcpAsync(id, string.Empty);
        Assert.Equal(string.Empty, await store.GetSessionEnabledMcpAsync(id));
    }

    [Fact]
    public async Task Schema_v6_is_stamped_and_reinit_is_idempotent()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var id = await store.CreateSessionAsync("s");

        Assert.Equal(6, await ReadUserVersionAsync());

        // 重复初始化：列已存在、版本已盖章，不应抛错且数据保留
        await store.InitializeAsync();
        Assert.Equal(6, await ReadUserVersionAsync());
        Assert.Single(await store.ListSessionsAsync());

        await store.SetSessionWorkspaceDirAsync(id, @"D:\ws");
        await store.SetSessionEnabledMcpAsync(id, @"[""litssh""]");
        await store.InitializeAsync();
        Assert.Equal(@"D:\ws", await store.GetSessionWorkspaceDirAsync(id));
        Assert.Equal(@"[""litssh""]", await store.GetSessionEnabledMcpAsync(id));
    }

    private async Task<long> ReadUserVersionAsync()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* ignore */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* ignore */ }
    }
}
