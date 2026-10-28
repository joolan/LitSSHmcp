using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 大模型调用中间件：并发上限（信号量）+ 单次超时 + 瞬时错误重试（网络/超时/限流/5xx）。
/// 流式仅在"首个增量到达前"重试；首包之后的错误直接抛出（避免重复内容）。
/// </summary>
public sealed class ResilientChatClient : DelegatingChatClient
{
    private readonly SemaphoreSlim _gate;
    private readonly bool _ownsGate;
    private readonly int _timeoutSeconds;
    private readonly int _maxRetries;

    /// <param name="sharedGate">跨会话共享的并发闸门（同一 provider/model 共用一个）；null=当前包装器独占。</param>
    public ResilientChatClient(IChatClient innerClient, int maxConcurrency, int timeoutSeconds, int maxRetries,
        SemaphoreSlim? sharedGate = null)
        : base(innerClient)
    {
        var concurrency = Math.Max(1, maxConcurrency);
        _gate = sharedGate ?? new SemaphoreSlim(concurrency, concurrency);
        _ownsGate = sharedGate is null;
        _timeoutSeconds = timeoutSeconds;
        _maxRetries = Math.Max(0, maxRetries);
    }

    /// <summary>
    /// OpenAI 官方 <c>prompt_cache_key</c>（请求顶层字段，用于前缀缓存分组路由）：
    /// 由 <see cref="ChatClientFactory"/> 按 provider 计算并设置，null=不注入（非官方端点）。
    /// 注意：值须跨会话/重启稳定，变化会打散缓存分组。
    /// </summary>
    public string? PromptCacheKey { get; set; }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        options = ApplyCacheKey(options);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var attempt = 0;
            while (true)
            {
                using var timeout = CreateTimeout(cancellationToken);
                try
                {
                    return await base.GetResponseAsync(list, options, timeout.Token);
                }
                catch (Exception ex)
                {
                    if (!ShouldRetry(ex, cancellationToken, ref attempt))
                        throw;
                    await DelayAsync(attempt, cancellationToken);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        options = ApplyCacheKey(options);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var attempt = 0;
            while (true)
            {
                using var timeout = CreateTimeout(cancellationToken);
                var enumerator = base.GetStreamingResponseAsync(list, options, timeout.Token).GetAsyncEnumerator(timeout.Token);

                bool hasFirst;
                try
                {
                    hasFirst = await enumerator.MoveNextAsync();
                }
                catch (Exception ex)
                {
                    await enumerator.DisposeAsync();
                    if (!ShouldRetry(ex, cancellationToken, ref attempt))
                        throw;
                    await DelayAsync(attempt, cancellationToken);
                    continue;
                }

                if (!hasFirst)
                {
                    await enumerator.DisposeAsync();
                    yield break;
                }

                yield return enumerator.Current;

                // 首个增量之后不再重试：直接透传（含可能的异常）
                while (true)
                {
                    var moved = await enumerator.MoveNextAsync();
                    if (!moved)
                        break;
                    yield return enumerator.Current;
                }

                await enumerator.DisposeAsync();
                yield break;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 若设置了 <see cref="PromptCacheKey"/> 且调用方未自带 RawRepresentationFactory，
    /// 则给 options 装一个 factory：用官方 JsonPatch 机制向请求体注入顶层 prompt_cache_key
    /// （AdditionalProperties 不会进请求体；其余字段仍由 M.E.AI 正常回填）。
    /// </summary>
    private ChatOptions? ApplyCacheKey(ChatOptions? options)
    {
        if (PromptCacheKey is null || options is null || options.RawRepresentationFactory is not null)
            return options;
        var key = PromptCacheKey;
        options.RawRepresentationFactory = _ =>
        {
            var raw = new OpenAI.Chat.ChatCompletionOptions();
            raw.Patch.Set("$.prompt_cache_key"u8, key);
            return raw;
        };
        return options;
    }

    private CancellationTokenSource CreateTimeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_timeoutSeconds > 0)
            cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
        return cts;
    }

    private bool ShouldRetry(Exception ex, CancellationToken callerToken, ref int attempt)
    {
        if (callerToken.IsCancellationRequested || attempt >= _maxRetries)
            return false;
        if (!ChatErrorClassifier.IsTransient(ex))
            return false;
        attempt++;
        return true;
    }

    private static Task DelayAsync(int attempt, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct);

    /// <summary>
    /// 不释放内部客户端：它由 <see cref="ChatClientFactory"/> 跨会话共享缓存，
    /// 若随包装器一起 Dispose 会让后续会话拿到已释放的连接（评估报告 §4.2 句柄泄漏修复的配套约束）。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsGate)
            _gate.Dispose();
    }
}
