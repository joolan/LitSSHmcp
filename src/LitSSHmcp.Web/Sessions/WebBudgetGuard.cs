using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Web.Sessions;

/// <summary>
/// Web 会话成本预算（评估报告 §10.B.6"LLM token 配额（agent.budget 按用户生效）"）：
/// 桌面在 UI 层按会话拦（AgentViewModel.SessionCostBudgetBlock），Web 无 UI 拦截点，
/// 改在发送前由运行时拦截。会话按登录用户隔离 + 每用户会话数上限（MaxSessionsPerUser）
/// → 单用户成本上界 = 会话上限 × agent.budget.maxSessionCost。
/// 模型未填价格无法判定成本时放行（与桌面一致）。
/// </summary>
public static class WebBudgetGuard
{
    /// <summary>达到上限时返回阻止原因（null=可继续）。</summary>
    public static string? ComputeCostBlock(AgentBudgetConfig? budget,
        long sessionInputTokens, long sessionOutputTokens, double inputPricePerMTok, double outputPricePerMTok)
    {
        var limit = budget?.MaxSessionCost ?? 0;
        if (limit <= 0)
            return null;
        if (inputPricePerMTok <= 0 && outputPricePerMTok <= 0)
            return null;   // 模型未填价格无法判定成本
        var cost = sessionInputTokens / 1_000_000.0 * inputPricePerMTok
                   + sessionOutputTokens / 1_000_000.0 * outputPricePerMTok;
        return cost >= limit
            ? $"会话估算成本已达预算上限 ¥{cost:F4} ≥ ¥{limit:F4}，已阻止发送（AI 助手设置 → 会话成本上限可调整）"
            : null;
    }
}
