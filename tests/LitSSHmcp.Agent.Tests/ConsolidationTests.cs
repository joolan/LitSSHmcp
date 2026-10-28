using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class ConsolidationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"litssh-consolidate-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* 忽略 */ }
        }
    }

    private async Task<(AgentMemoryStore Store, AgentMemoryService Memory)> CreateAsync()
    {
        var store = new AgentMemoryStore(_dbPath);
        var memory = new AgentMemoryService(store, new FixedEmbedder(), 5, 0.15);
        await memory.InitializeAsync();
        return (store, memory);
    }

    private static AgentConsolidationConfig Config() => new()
    {
        Enabled = true,
        MinRawCount = 1,
        MaxRawPerRun = 20,
        MaxCandidatesPerRun = 10,
        MinIntervalMinutes = 0
    };

    [Fact]
    public async Task Rule_based_run_creates_pending_candidate_and_marks_raw_consolidated()
    {
        var (store, memory) = await CreateAsync();
        await memory.IndexAsync("会话:s1", "用户: ssh:web01 磁盘满 df 100% 助手: 已清理 journal 后恢复");

        var service = new AgentConsolidationService(store, client: null, Config());
        var result = await service.RunAsync(force: true);

        Assert.Contains("候选 1", result);
        var candidate = Assert.Single(await store.ListConsolidationCandidatesAsync("pending", 10));
        Assert.Equal("incident", candidate.Kind);
        Assert.Contains("ssh:web01", candidate.AssetIds);
        Assert.Contains("raw=1", candidate.SourceRefs);
        Assert.Contains("raw:1", candidate.Evidence);
        Assert.Contains("磁盘", candidate.PayloadJson);
        Assert.Empty(await store.ListUnconsolidatedRawAsync(10));
    }

    [Fact]
    public async Task Daily_run_quota_and_run_history_are_enforced_and_visible()
    {
        var (store, memory) = await CreateAsync();
        await memory.IndexAsync("会话:s1", "用户: ssh:web01 磁盘满 df 100% 助手: 已清理 journal 后恢复");
        var config = Config();
        config.MaxRunsPerDay = 1;
        var service = new AgentConsolidationService(store, client: null, config);

        var first = await service.RunAsync(force: false);
        var second = await service.RunAsync(force: false);

        Assert.Contains("候选 1", first);
        Assert.Contains("今日 consolidation 已运行", second);
        var runs = await store.ListConsolidationRunsAsync(10);
        var run = Assert.Single(runs);
        Assert.Equal("completed", run.Status);
        Assert.Equal(1, run.RawCount);
        var usage = await store.GetConsolidationUsageSinceAsync(DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal(1, usage.Runs);
    }

    [Fact]
    public async Task Llm_extraction_creates_pending_candidate_without_auto_apply()
    {
        var (store, memory) = await CreateAsync();
        await memory.IndexAsync("会话:s1", "用户: ssh:web01 磁盘满 助手: journalctl --vacuum-size 后恢复");
        var client = new FakeChatClient("""
            {"title":"web01 磁盘满恢复","symptom":"df 100%","rootCause":"journald 膨胀","actions":"journalctl --vacuum-size=200M","outcome":"恢复","evidence":"log_query 输出","confidence":0.9,"status":"resolved"}
            """);

        var service = new AgentConsolidationService(store, client, Config());
        var result = await service.RunAsync(force: true);

        Assert.Contains("候选 1", result);
        Assert.Empty(await store.ListIncidentsAsync(null, null, 10));   // 不再自动写入事件
        var candidate = Assert.Single(await store.ListConsolidationCandidatesAsync("pending", 10));
        Assert.Equal("incident", candidate.Kind);
        Assert.Contains("web01 磁盘满恢复", candidate.PayloadJson);
    }

    [Fact]
    public async Task Dedupe_prevents_duplicate_candidates()
    {
        var (store, memory) = await CreateAsync();
        await memory.IndexAsync("会话:s1", "用户: ssh:web01 磁盘满 助手: 已恢复");
        var service = new AgentConsolidationService(store, client: null, Config());

        await service.RunAsync(force: true);
        var second = await service.RunAsync(force: true);

        Assert.Contains("没有待整理", second);
        Assert.Single(await store.ListConsolidationCandidatesAsync(null, 10));
    }

    [Fact]
    public async Task Run_respects_min_raw_threshold_unless_forced()
    {
        var (store, memory) = await CreateAsync();
        await memory.IndexAsync("会话:s1", "用户: ssh:web01 磁盘满 助手: 已恢复");
        var config = Config();
        config.MinRawCount = 5;
        var service = new AgentConsolidationService(store, client: null, config);

        var skipped = await service.RunAsync(force: false);
        Assert.Contains("少于阈值", skipped);

        var forced = await service.RunAsync(force: true);
        Assert.Contains("候选 1", forced);
    }

    [Fact]
    public async Task Consolidation_candidates_are_scoped_private()
    {
        var aliceStore = new AgentMemoryStore(_dbPath, new MemoryAccessScope("1", IsAdmin: false));
        var aliceMemory = new AgentMemoryService(aliceStore, new FixedEmbedder(), 5, 0.15);
        await aliceMemory.InitializeAsync();
        await aliceMemory.IndexAsync("会话:s1", "用户: ssh:web01 磁盘满 助手: 已恢复");
        var alice = new AgentConsolidationService(aliceStore, client: null, Config());
        await alice.RunAsync(force: true);

        var bobStore = new AgentMemoryStore(_dbPath, new MemoryAccessScope("2", IsAdmin: false));
        Assert.Empty(await bobStore.ListConsolidationCandidatesAsync(null, 10));
        Assert.Single(await aliceStore.ListConsolidationCandidatesAsync(null, 10));
    }

    [Fact]
    public async Task Llm_extraction_creates_asset_fact_and_script_candidates()
    {
        var (store, memory) = await CreateAsync();
        await memory.IndexAsync("会话:s1",
            "用户: ssh:web01 的日志路径是 /var/log/app.log，磁盘满时用 journalctl --vacuum-size=200M 清理 助手: 已恢复");
        var client = new FakeChatClient("""
            {
              "title":"web01 磁盘满恢复",
              "symptom":"df 100%",
              "confidence":0.9,
              "status":"resolved",
              "assetFacts":[{"assetId":"ssh:web01","key":"日志路径","value":"/var/log/app.log","confidence":0.8}],
              "scriptCandidate":{"title":"清理 journal 日志","purpose":"磁盘满时释放 journal 空间","language":"bash","content":"journalctl --vacuum-size=200M","confidence":0.7}
            }
            """);
        var service = new AgentConsolidationService(store, client, Config());

        await service.RunAsync(force: true);

        var candidates = await store.ListConsolidationCandidatesAsync(null, 10);
        Assert.Contains(candidates, c => c.Kind == "asset_fact");
        Assert.Contains(candidates, c => c.Kind == "script");
    }

    [Fact]
    public async Task Review_approves_asset_fact_and_script_into_memory()
    {
        var (store, memory) = await CreateAsync();
        await memory.IndexAsync("会话:s1",
            "用户: ssh:web01 日志路径 /var/log/app.log 助手: 已恢复");
        var client = new FakeChatClient("""
            {
              "title":"web01 事件","symptom":"测试","confidence":0.9,"status":"resolved",
              "assetFacts":[{"assetId":"ssh:web01","key":"日志路径","value":"/var/log/app.log","confidence":0.8}],
              "scriptCandidate":{"title":"清理日志","purpose":"释放空间","language":"bash","content":"journalctl --vacuum-size=200M","confidence":0.7}
            }
            """);
        var consolidation = new AgentConsolidationService(store, client, Config());
        await consolidation.RunAsync(force: true);
        var review = new ConsolidationReviewService(store, memory);

        var fact = (await store.ListConsolidationCandidatesAsync(null, 10)).Single(c => c.Kind == "asset_fact");
        var factResult = await review.ApproveAsync(fact.Id);
        Assert.True(factResult.Ok);
        var factMemory = await store.FindByKeyAsync("asset", "资产事实:ssh:web01:日志路径");
        Assert.NotNull(factMemory);
        Assert.Contains("ssh:web01", factMemory!.AssetIds);

        var script = (await store.ListConsolidationCandidatesAsync(null, 10)).Single(c => c.Kind == "script");
        var scriptResult = await review.ApproveAsync(script.Id);
        Assert.True(scriptResult.Ok);
        var scriptMemory = await store.FindByKeyAsync("script", "脚本:清理日志");
        Assert.NotNull(scriptMemory);
        Assert.Contains("journalctl", scriptMemory!.Content);
    }

    [Fact]
    public async Task Review_can_approve_or_reject_pending_candidates()
    {
        var (store, memory) = await CreateAsync();
        await memory.IndexAsync("会话:s1", "用户: ssh:web01 磁盘满 助手: 已恢复");
        var consolidation = new AgentConsolidationService(store, client: null, Config());
        await consolidation.RunAsync(force: true);
        var candidate = Assert.Single(await store.ListConsolidationCandidatesAsync("pending", 10));
        var review = new ConsolidationReviewService(store);

        var approved = await review.ApproveAsync(candidate.Id);
        Assert.True(approved.Ok);
        Assert.NotNull(approved.IncidentId);
        var incident = await store.GetIncidentAsync(approved.IncidentId!.Value);
        Assert.NotNull(incident);
        Assert.Equal("pending", candidate.Status);   // 快照记录，不是实时状态
        Assert.Equal("applied", (await store.GetConsolidationCandidateAsync(candidate.Id))!.Status);

        await memory.IndexAsync("会话:s2", "用户: ssh:web02 证书过期 助手: 已续期");
        await consolidation.RunAsync(force: true);
        var second = (await store.ListConsolidationCandidatesAsync("pending", 10)).Single();
        var rejected = await review.RejectAsync(second.Id, "重复事件");
        Assert.True(rejected.Ok);
        var after = await store.GetConsolidationCandidateAsync(second.Id);
        Assert.Equal("rejected", after!.Status);
        Assert.Contains("重复事件", after.RejectReason);
    }

    [Fact]
    public async Task Review_supersedes_incident()
    {
        var (store, _) = await CreateAsync();
        var oldId = await store.AddIncidentAsync("旧", "现象", "ssh:s1", "", "", "", "", 0.5, "resolved", "");
        var newId = await store.AddIncidentAsync("新", "现象", "ssh:s1", "", "", "", "", 0.9, "resolved", "");
        var review = new ConsolidationReviewService(store);

        var result = await review.SupersedeIncidentAsync(oldId, newId);

        Assert.True(result.Ok);
        var old = await store.GetIncidentAsync(oldId);
        Assert.Equal("superseded", old!.Status);
        Assert.Equal(newId, old.SupersededBy);
    }

    private sealed class FixedEmbedder : IEmbeddingGenerator<string, Embedding<float>>, IDisposable
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

    private sealed class FakeChatClient(string json) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new List<ChatMessage> { new(ChatRole.Assistant, json) }));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, new AIContent[] { new TextContent(json) });
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
