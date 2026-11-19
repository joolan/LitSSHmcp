using System.IO;
using LitSSHmcp.App.Services.DatasourceAi;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class DatasourceAiStoreTests
{
    [Fact]
    public async Task Session_messages_and_history_are_scoped_by_datasource_database()
    {
        var path = Path.Combine(Path.GetTempPath(), $"litssh-dsai-{Guid.NewGuid():N}.db");
        try
        {
            var store = new DatasourceAiStore(path);
            var sessionId = await store.CreateSessionAsync("ds1", "mysql", "shop", "排查订单查询");
            await store.AppendMessageAsync(sessionId, "user", "为什么慢？");
            await store.AppendMessageAsync(sessionId, "assistant", "先看索引。");
            var other = await store.CreateSessionAsync("ds2", "postgres", "shop", "另一个数据源");

            var sessions = await store.ListSessionsAsync("ds1", "mysql", "shop");
            Assert.Single(sessions);
            Assert.Equal(sessionId, sessions[0].Id);
            Assert.Equal("排查订单查询", sessions[0].Title);

            var latest = await store.GetLatestSessionAsync("ds1", "mysql", "shop");
            Assert.NotNull(latest);
            Assert.Equal(sessionId, latest!.Id);
            Assert.Null(await store.GetLatestSessionAsync("ds1", "mysql", "other"));

            var messages = await store.LoadMessagesAsync(sessionId);
            Assert.Equal(2, messages.Count);
            Assert.Equal("user", messages[0].Role);
            Assert.Equal("为什么慢？", messages[0].Content);
            Assert.Equal("assistant", messages[1].Role);

            Assert.NotEqual(sessionId, other);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Rename_session_updates_history_title()
    {
        var path = Path.Combine(Path.GetTempPath(), $"litssh-dsai-{Guid.NewGuid():N}.db");
        try
        {
            var store = new DatasourceAiStore(path);
            var id = await store.CreateSessionAsync("ds1", "mysql", "shop", "与 Shop 对话");
            await store.RenameSessionAsync(id, "订单慢查询分析");

            var latest = await store.GetLatestSessionAsync("ds1", "mysql", "shop");
            Assert.Equal("订单慢查询分析", latest?.Title);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Delete_session_removes_session_and_messages()
    {
        var path = Path.Combine(Path.GetTempPath(), $"litssh-dsai-{Guid.NewGuid():N}.db");
        try
        {
            var store = new DatasourceAiStore(path);
            var id = await store.CreateSessionAsync("ds1", "mysql", "shop", "待删除");
            await store.AppendMessageAsync(id, "user", "hello");
            await store.DeleteSessionAsync(id);

            Assert.Null(await store.GetLatestSessionAsync("ds1", "mysql", "shop"));
            Assert.Empty(await store.LoadMessagesAsync(id));
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
        try { File.Delete(path + "-wal"); } catch { /* ignore */ }
        try { File.Delete(path + "-shm"); } catch { /* ignore */ }
    }
}

public class DatasourceAiContextBuilderTests
{
    [Fact]
    public void Result_digest_masks_sensitive_columns_and_limits_rows()
    {
        var result = new DatasourceQueryResult
        {
            Success = true,
            Columns = new[] { "id", "email", "note" },
            RowCount = 1000,
            Truncated = true,
            DurationMs = 12,
            Rows = Enumerable.Range(1, 30).Select(i => new Dictionary<string, object?>
            {
                ["id"] = i,
                ["email"] = $"user{i}@example.com",
                ["note"] = "ok"
            }).ToList()
        };
        var masking = new MaskingConfig
        {
            Rules = new[] { new MaskRule { Column = "email", Mode = "email" } }
        };

        var digest = DatasourceAiContextBuilder.BuildResultDigest(result, masking);

        Assert.Contains("本次查询取回：1,000 行", digest);
        Assert.Contains("查询截断：是", digest);
        Assert.Contains("AI 可见：前 20 行", digest);
        Assert.DoesNotContain("user1@example.com", digest);
        Assert.Contains("u***@example.com", digest);
    }

    [Fact]
    public void Result_digest_marks_selected_row_subset()
    {
        var result = new DatasourceQueryResult
        {
            Success = true,
            Columns = new[] { "id" },
            RowCount = 30,
            Rows = new List<Dictionary<string, object?>> { new() { ["id"] = 7 } }
        };

        var digest = DatasourceAiContextBuilder.BuildResultDigest(
            result, null, maxRows: 5, isSubset: true);

        Assert.Contains("AI 可见：1 行（用户选择的行", digest);
        Assert.Contains("选中行：", digest);
    }

    [Fact]
    public void Result_digest_includes_error_without_rows()
    {
        var result = new DatasourceQueryResult
        {
            Success = false,
            Error = "syntax error",
            Columns = Array.Empty<string>(),
            Rows = new List<Dictionary<string, object?>>()
        };

        var digest = DatasourceAiContextBuilder.BuildResultDigest(result, null);

        Assert.Contains("成功：否", digest);
        Assert.Contains("syntax error", digest);
        Assert.Contains("（无结果行）", digest);
    }
}
