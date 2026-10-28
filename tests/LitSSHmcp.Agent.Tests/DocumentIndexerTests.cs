using LitSSHmcp.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class DocumentIndexerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"litssh-doc-{Guid.NewGuid():N}.db");
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"litssh-ws-{Guid.NewGuid():N}");

    public DocumentIndexerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 忽略 */ }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* 忽略 */ }
        }
    }

    private async Task<(AgentMemoryService Svc, AgentMemoryStore Store)> CreateAsync(string model = "test-embed")
    {
        var store = new AgentMemoryStore(_dbPath);
        var svc = new AgentMemoryService(store, new FakeEmbedder(), 5, 0.15, model);
        await svc.InitializeAsync();
        return (svc, store);
    }

    [Fact]
    public async Task Indexes_text_files_in_chunks_and_skips_binary()
    {
        var longText = "# 磁盘巡检\n" + string.Join("\n", Enumerable.Range(0, 80).Select(i =>
            $"第{i}行: /var/log 磁盘占用排查与清理步骤，内容用于测试分块。"));
        await File.WriteAllTextAsync(Path.Combine(_root, "a.md"), longText);
        await File.WriteAllTextAsync(Path.Combine(_root, "b.txt"), "短文档内容");
        await File.WriteAllBytesAsync(Path.Combine(_root, "c.png"), new byte[] { 1, 2, 3 });
        var (svc, store) = await CreateAsync();

        var indexed = await new DocumentIndexer(svc).IndexAsync(_root);

        Assert.True(indexed >= 2);
        var rows = await store.ListAsync(null, null, 500, includeRaw: true);
        Assert.Contains(rows, r => r.Source.StartsWith("文档:a.md#", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Source == "文档:b.txt#0");
        Assert.DoesNotContain(rows, r => r.Source.Contains("c.png", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unchanged_file_is_not_reindexed()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.md"), "# 标题\n未变化的内容");
        var (svc, store) = await CreateAsync();
        var indexer = new DocumentIndexer(svc);

        await indexer.IndexAsync(_root);
        var before = (await store.ListAsync(null, null, 500, includeRaw: true)).Count;
        var second = await indexer.IndexAsync(_root);

        Assert.Equal(0, second);
        Assert.Equal(before, (await store.ListAsync(null, null, 500, includeRaw: true)).Count);
    }

    [Fact]
    public async Task Changed_file_replaces_old_chunks()
    {
        var path = Path.Combine(_root, "a.md");
        await File.WriteAllTextAsync(path, "# 标题\nv1 旧内容");
        var (svc, store) = await CreateAsync();
        var indexer = new DocumentIndexer(svc);

        await indexer.IndexAsync(_root);
        await File.WriteAllTextAsync(path, "# 标题\nv2 新内容");
        var indexed = await indexer.IndexAsync(_root);

        Assert.Equal(1, indexed);
        var rows = await store.ListAsync(null, null, 500, includeRaw: true);
        Assert.DoesNotContain(rows, r => r.Content.Contains("v1 旧内容", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Content.Contains("v2 新内容", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Legacy_whole_file_source_is_removed_after_reindex()
    {
        var path = Path.Combine(_root, "a.md");
        await File.WriteAllTextAsync(path, "# 标题\n新索引内容");
        var (svc, store) = await CreateAsync();
        await svc.IndexAsync("文档:a.md", "旧版整文件索引内容");

        await new DocumentIndexer(svc).IndexAsync(_root);

        var rows = await store.ListAsync(null, null, 500, includeRaw: true);
        Assert.DoesNotContain(rows, r => r.Source == "文档:a.md");
        Assert.Contains(rows, r => r.Source == "文档:a.md#0");
    }

    [Fact]
    public async Task Document_secrets_are_redacted_before_indexing()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.md"),
            "# 连接说明\nmysql://app:supersecret@db1:3306/order\n");
        var (svc, store) = await CreateAsync();

        await new DocumentIndexer(svc).IndexAsync(_root);

        var rows = await store.ListAsync(null, null, 500, includeRaw: true);
        var content = string.Join("\n", rows.Select(r => r.Content));
        Assert.DoesNotContain("supersecret", content, StringComparison.Ordinal);
        Assert.Contains("******", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Embedding_model_change_rebuilds_document_chunks()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.md"), "# 标题\n模型变更测试");
        var (svc1, store1) = await CreateAsync("model-a");
        await new DocumentIndexer(svc1).IndexAsync(_root);
        var before = (await store1.ListAsync(null, null, 500, includeRaw: true)).Count;

        var (svc2, store2) = await CreateAsync("model-b");
        var indexed = await new DocumentIndexer(svc2).IndexAsync(_root);

        Assert.Equal(1, indexed);
        Assert.Equal(before, (await store2.ListAsync(null, null, 500, includeRaw: true)).Count);
    }

    private sealed class FakeEmbedder : IEmbeddingGenerator<string, Embedding<float>>, IDisposable
    {
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = values.Select(v => new Embedding<float>(Hash(v))).ToList();
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
        }

        private static float[] Hash(string text)
        {
            var v = new float[256];
            foreach (var c in text.ToLowerInvariant())
                v[(byte)c % 256] += 1f;
            var norm = MathF.Sqrt(v.Sum(x => x * x));
            if (norm > 0)
                for (var i = 0; i < v.Length; i++) v[i] /= norm;
            return v;
        }
    }
}
