using LitSSHmcp.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

/// <summary>
/// 本地工具合并后的清单门禁：工作区 2（read/write）、落盘 1（action）、记忆 3，旧工具名不得再出现。
/// </summary>
public class LocalToolInventoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litssh-tools-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public LocalToolInventoryTests()
    {
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "agent.db");
    }

    [Fact]
    public void Workspace_exposes_read_and_write_only()
    {
        var tools = WorkspaceTools.Create(Path.Combine(_root, "ws"));
        Assert.Equal(new[] { "ops_doc_read", "ops_doc_write" }, tools.Select(t => t.Name).ToArray());
        Assert.True(tools.Single(t => t.Name == "ops_doc_read").ReadOnly);
        Assert.False(tools.Single(t => t.Name == "ops_doc_write").ReadOnly);
        Assert.All(tools, t => Assert.True(t.IsLocal));
        Assert.DoesNotContain("ops_doc_list", tools.Select(t => t.Name));
        Assert.DoesNotContain("ops_doc_append", tools.Select(t => t.Name));
        Assert.DoesNotContain("ops_doc_patch", tools.Select(t => t.Name));
    }

    [Fact]
    public async Task Workspace_write_dispatches_modes_and_rejects_unknown()
    {
        var tools = WorkspaceTools.Create(Path.Combine(_root, "ws"));
        var write = tools.Single(t => t.Name == "ops_doc_write");

        Assert.Contains("已写入", Text(await write.InvokeAsync(Args(("content", "a")), default)));
        Assert.Contains("已追加", Text(await write.InvokeAsync(Args(("mode", "append"), ("content", "b")), default)));
        Assert.Contains("已更新", Text(await write.InvokeAsync(
            Args(("mode", "patch"), ("find", "a"), ("replace", "A")), default)));
        Assert.Contains("仅支持 overwrite/append/patch",
            Text(await write.InvokeAsync(Args(("mode", "delete"), ("content", "x")), default)));
        Assert.Contains("需要 content",
            Text(await write.InvokeAsync(Args(("mode", "append")), default)));

        var content = await File.ReadAllTextAsync(Path.Combine(_root, "ws", WorkspaceTools.DefaultDoc));
        Assert.Contains("A", content);
        Assert.Contains("b", content);
        Assert.DoesNotContain("a", content);
    }

    [Fact]
    public async Task Spill_exposes_single_action_tool()
    {
        var store = new SpillStore(Path.Combine(_root, "spills"), retentionDays: 0);
        var tools = SpillTools.Create(store);
        Assert.Equal(new[] { "spill" }, tools.Select(t => t.Name).ToArray());
        Assert.All(tools, t => Assert.True(t.ReadOnly));
        Assert.DoesNotContain("spill_grep", tools.Select(t => t.Name));

        var handle = store.Save("test", "line-1\nline-2");
        var spill = tools.Single(t => t.Name == "spill");
        Assert.Contains("line-1", Text(await spill.InvokeAsync(Args(("handle", handle)), default)));
        Assert.Contains("line-2", Text(await spill.InvokeAsync(
            Args(("action", "grep"), ("handle", handle), ("pattern", "line-2")), default)));
        Assert.Contains("仅支持 list/read/grep",
            Text(await spill.InvokeAsync(Args(("action", "delete"), ("handle", handle)), default)));
    }

    [Fact]
    public async Task Memory_exposes_write_list_and_write_dispatches_actions()
    {
        var store = new AgentMemoryStore(_dbPath);
        var service = new AgentMemoryService(store, new FakeEmbedder(), topK: 5);
        await service.InitializeAsync();
        var tools = MemoryTools.Create(service);

        Assert.Equal(new[] { "ops_memory_list", "ops_memory_write" },
            tools.Select(t => t.Name).ToArray());
        Assert.DoesNotContain("ops_memory_save", tools.Select(t => t.Name));
        Assert.DoesNotContain("ops_memory_update", tools.Select(t => t.Name));
        Assert.DoesNotContain("ops_memory_delete", tools.Select(t => t.Name));

        var write = tools.Single(t => t.Name == "ops_memory_write");
        Assert.Contains("已新增", Text(await write.InvokeAsync(Args(
            ("action", "save"), ("kind", "task"), ("key", "disk"), ("content", "磁盘满排查与恢复路径")), default)));
        Assert.Contains("已更新", Text(await write.InvokeAsync(Args(
            ("action", "update"), ("kind", "task"), ("key", "disk"), ("status", "superseded")), default)));
        Assert.Contains("仅支持 save/update/delete",
            Text(await write.InvokeAsync(Args(("action", "archive"), ("kind", "task"), ("key", "disk")), default)));
        Assert.Contains("已删除", Text(await write.InvokeAsync(Args(
            ("action", "delete"), ("kind", "task"), ("key", "disk")), default)));

        Assert.Null(await store.FindByKeyAsync("task", "disk"));
    }

    private static string Text(object? value) => value?.ToString() ?? string.Empty;

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value);

    private sealed class FakeEmbedder : IEmbeddingGenerator<string, Embedding<float>>, IDisposable
    {
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = values.Select(_ => new Embedding<float>(new[] { 1f, 0f })).ToList();
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
