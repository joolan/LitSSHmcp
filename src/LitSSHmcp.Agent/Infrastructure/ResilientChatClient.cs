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
    private readonly int _timeoutSeconds;
    private readonly int _maxRetries;

    public ResilientChatClient(IChatClient innerClient, int maxConcurrency, int timeoutSeconds, int maxRetries)
        : base(innerClient)
    {
        var concurrency = Math.Max(1, maxConcurrency);
        _gate = new SemaphoreSlim(concurrency, concurrency);
        _timeoutSeconds = timeoutSeconds;
        _maxRetries = Math.Max(0, maxRetries);
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
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
}
