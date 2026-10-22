namespace LitSSHmcp.Agent;

/// <summary>
/// 父子会话/并行子代理共享的成本账本（P2-3 收尾）：每次模型响应把增量 token 原子累加，
/// 预算检查读取全局总量，避免父会话与多个子代理各算各的造成并发超额。
/// </summary>
public sealed class SessionCostBudget
{
    private readonly double _limit;
    private readonly double _inputPricePerMTok;
    private readonly double _outputPricePerMTok;
    private long _inputTokens;
    private long _outputTokens;

    public SessionCostBudget(double limit, double inputPricePerMTok, double outputPricePerMTok)
    {
        _limit = limit;
        _inputPricePerMTok = inputPricePerMTok;
        _outputPricePerMTok = outputPricePerMTok;
    }

    public long InputTokens => Interlocked.Read(ref _inputTokens);
    public long OutputTokens => Interlocked.Read(ref _outputTokens);

    public double Cost => CostOf(InputTokens, OutputTokens);

    public void Add(long inputTokens, long outputTokens)
    {
        if (inputTokens > 0)
            Interlocked.Add(ref _inputTokens, inputTokens);
        if (outputTokens > 0)
            Interlocked.Add(ref _outputTokens, outputTokens);
    }

    /// <summary>当前全局成本是否仍低于上限。</summary>
    public bool Check() => Cost < _limit;

    public double CostOf(long inputTokens, long outputTokens)
        => inputTokens / 1_000_000.0 * _inputPricePerMTok
           + outputTokens / 1_000_000.0 * _outputPricePerMTok;
}
