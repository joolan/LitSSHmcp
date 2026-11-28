using System.ClientModel;
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
                catch (Exception caught)
                {
                    var ex = NormalizeTimeout(caught, cancellationToken, timeout);
                    if (!ShouldRetry(ex, cancellationToken, ref attempt))
                    {
                        if (!ReferenceEquals(ex, caught)) throw ex;
                        throw;
                    }
                    await DelayAsync(attempt, ex, cancellationToken);
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
                catch (Exception caught)
                {
                    await enumerator.DisposeAsync();
                    var ex = NormalizeTimeout(caught, cancellationToken, timeout);
                    if (!ShouldRetry(ex, cancellationToken, ref attempt))
                    {
                        if (!ReferenceEquals(ex, caught)) throw ex;
                        throw;
                    }
                    await DelayAsync(attempt, ex, cancellationToken);
                    continue;
                }

                if (!hasFirst)
                {
                    await enumerator.DisposeAsync();
                    yield break;
                }

                yield return enumerator.Current;

                // 首个增量之后不再重试：直接透传（含可能的异常；超时统一转 TimeoutException 以与用户取消区分）
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync();
                    }
                    catch (Exception caught)
                    {
                        await enumerator.DisposeAsync();
                        var ex = NormalizeTimeout(caught, cancellationToken, timeout);
                        if (!ReferenceEquals(ex, caught)) throw ex;
                        throw;
                    }
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

    /// <summary>
    /// 内部超时 CTS 触发的 OperationCanceledException 与用户取消共享同一异常类型，
    /// 会被下游归类为“已取消/已停止”且不参与重试 → 转为 TimeoutException，
    /// 让分类器识别为 Timeout（可重试、给出正确提示）。
    /// </summary>
    private Exception NormalizeTimeout(Exception ex, CancellationToken callerToken, CancellationTokenSource timeout)
    {
        if (ex is OperationCanceledException && !callerToken.IsCancellationRequested && timeout.IsCancellationRequested)
            return new TimeoutException($"模型请求超时（{_timeoutSeconds}s 内未完成）", ex);
        return ex;
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

    /// <summary>退避策略：429/503 带 Retry-After 头时按头等待；否则指数退避(300ms 起步、8s 封顶)加 ±15% jitter。
    /// 旧实现线性 300ms×attempt 且无视 Retry-After，限流场景下重试必然二次撞墙。</summary>
    private static Task DelayAsync(int attempt, Exception? lastError, CancellationToken ct) =>
        Task.Delay(RetryDelay(lastError, attempt), ct);

    internal static TimeSpan RetryDelay(Exception? lastError, int attempt)
    {
        var retryAfter = TryGetRetryAfter(lastError);
        if (retryAfter is { } header && header > TimeSpan.Zero && header <= TimeSpan.FromMinutes(2))
            return header;

        var baseMs = Math.Min(300d * Math.Pow(2, Math.Max(1, attempt) - 1), 8000);
        var jitter = 0.85 + Random.Shared.NextDouble() * 0.3;   // 0.85~1.15
        return TimeSpan.FromMilliseconds(baseMs * jitter);
    }

    /// <summary>从限流/服务端错误响应里提取 Retry-After（秒数形态；主流 LLM 网关都用秒数，HTTP-date 形态忽略）。</summary>
    private static TimeSpan? TryGetRetryAfter(Exception? lastError)
    {
        if (lastError is not ClientResultException cre)
            return null;
        try
        {
            if (cre.GetRawResponse()?.Headers is not { } headers ||
                !headers.TryGetValues("Retry-After", out var values))
                return null;
            var text = values?.FirstOrDefault();
            return double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : null;
        }
        catch
        {
            return null;
        }
    }

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
