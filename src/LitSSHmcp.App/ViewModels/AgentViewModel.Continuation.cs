namespace LitSSHmcp.App.ViewModels;

public partial class AgentViewModel
{
    /// <summary>工具调用达到单轮上限时，在当前轮次输入区展示继续/停止卡片。</summary>
    private Task<bool> RequestToolLimitApprovalAsync(
        SessionRunContext ctx, int used, int limit, CancellationToken ct)
    {
        ctx.SetState(SessionRunState.WaitingApproval);
        var request = new ToolLimitApprovalRequest(used, limit);

        void Show()
        {
            try
            {
                if (ctx.ActiveTurn is { } turn)
                {
                    turn.ToolLimitRequest = request;
                    RaisePendingToolLimitChanged();
                    return;
                }
            }
            catch
            {
                // 无活动轮次时按停止处理（不会自动放大工具预算）
            }
            request.Resolve(false, "没有可用的活动轮次，已停止");
        }

        var registration = ct.Register(request.Cancel);
        _ = request.DecisionTask.ContinueWith(_ => registration.Dispose(), TaskContinuationOptions.ExecuteSynchronously);
        _ = request.DecisionTask.ContinueWith(_ => ctx.SetState(SessionRunState.Running),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _ = request.DecisionTask.ContinueWith(_ => _uiInvoke(RaisePendingToolLimitChanged),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _uiBeginInvoke(Show);
        return request.DecisionTask;
    }
}
