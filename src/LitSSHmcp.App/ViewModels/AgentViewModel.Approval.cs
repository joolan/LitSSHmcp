using LitSSHmcp.App.Services.LocalTerminal;

namespace LitSSHmcp.App.ViewModels;

public partial class AgentViewModel
{
    /// <summary>当前会话等待中的本机命令审批（吸附加载到输入区上方）。</summary>
    public LocalApprovalRequest? PendingApproval =>
        CurrentContext.ActiveTurn?.Approvals.LastOrDefault(a => a.IsPending);

    public bool HasPendingApproval => PendingApproval is not null;

    private void RaisePendingApprovalChanged()
    {
        OnPropertyChanged(nameof(PendingApproval));
        OnPropertyChanged(nameof(HasPendingApproval));
    }

    /// <summary>
    /// 本机终端审批：把请求挂到当前轮次的内联卡片；无可用轮次时回退模态窗口。
    /// 工具线程只 await TaskCompletionSource，取消/超时仍按拒绝处理。
    /// </summary>
    private Task<LocalApprovalDecision> RequestLocalApprovalAsync(
        SessionRunContext ctx, string command, string workingDirectory, CancellationToken ct)
    {
        ctx.SetState(SessionRunState.WaitingApproval);
        var request = new LocalApprovalRequest(
            command, workingDirectory, ctx.SessionKey, _localExemptions.SessionAllGranted(ctx.SessionKey));

        void Show()
        {
            try
            {
                if (ctx.ActiveTurn is { } turn)
                {
                    turn.Approvals.Add(request);
                    RaisePendingApprovalChanged();
                    return;
                }

                var decision = LitSSHmcp.App.Views.LocalCommandApprovalWindow.Prompt(
                    command, workingDirectory, _localExemptions.SessionAllGranted(ctx.SessionKey), ctx.SessionKey);
                request.Resolve(decision);
            }
            catch (Exception)
            {
                request.Resolve(LocalApprovalDecision.Deny);
            }
        }

        var registration = ct.Register(request.Cancel);
        _ = request.DecisionTask.ContinueWith(_ => registration.Dispose(), TaskContinuationOptions.ExecuteSynchronously);
        _ = request.DecisionTask.ContinueWith(_ => ctx.SetState(SessionRunState.Running),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _ = request.DecisionTask.ContinueWith(_ => _uiInvoke(RaisePendingApprovalChanged),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _uiBeginInvoke(Show);
        return request.DecisionTask;
    }
}
