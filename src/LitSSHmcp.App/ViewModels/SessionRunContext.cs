using System.Collections.ObjectModel;
using System.Text;
using LitSSHmcp.Agent;

namespace LitSSHmcp.App.ViewModels;

/// <summary>
/// 会话运行状态机（评估报告 §4.1 路线 1）：排队 / 执行中 / 待审批 / 完成 / 失败 / 停止。
/// 状态只由 UI 线程变更；<see cref="SessionRunContext.StateChanged"/> 在 UI 线程回调。
/// </summary>
public enum SessionRunState
{
    Idle,
    Queued,
    Running,
    WaitingApproval,
    Done,
    Failed,
    Stopped
}

/// <summary>
/// 每会话运行上下文（runtime 池的池元素）：该会话的独立 <see cref="AgentRuntime"/>（含 MCP 子进程）、
/// 取消源、状态机、流式缓冲，以及该会话的“权威”轮次/计划镜像（切换会话后运行仍继续写这里，
/// 切回时据此恢复显示；落库也以此为准，杜绝后台完成轮写错当前会话）。
/// 池与状态变更都在 UI 线程（AgentViewModel 是 WPF ViewModel），无锁字典即可。
/// </summary>
public sealed class SessionRunContext
{
    public SessionRunContext(long sessionId) => SessionId = sessionId;

    /// <summary>会话 Id；0 = “未保存的新任务”草稿上下文。</summary>
    public long SessionId { get; }

    /// <summary>审计归因键（如 agent-42）；草稿态无归因。</summary>
    public string? AuditId => SessionId > 0 ? $"agent-{SessionId}" : null;

    /// <summary>本会话键（本地终端按会话隔离用）：已落库=会话 Id，草稿="new"。</summary>
    public string SessionKey => SessionId > 0 ? SessionId.ToString() : "new";

    /// <summary>该会话的独立运行时；null = 尚未装配。</summary>
    public AgentRuntime? Runtime { get; set; }

    /// <summary>该会话当前运行的取消源（排队/执行/审批期间非空）。</summary>
    public CancellationTokenSource? Cts { get; set; }

    /// <summary>运行状态机当前值。</summary>
    public SessionRunState State { get; private set; } = SessionRunState.Idle;

    /// <summary>状态是否占用会话闸门（排队/执行/待审批均视为“任务进行中”）。</summary>
    public bool IsBusy => State is SessionRunState.Queued or SessionRunState.Running or SessionRunState.WaitingApproval;

    /// <summary>状态机变化（UI 线程）：宿主据此刷新 IsBusy 派生通知与侧栏徽章。</summary>
    public event Action<SessionRunContext>? StateChanged;

    public void SetState(SessionRunState state)
    {
        if (State == state)
            return;
        State = state;
        StateChanged?.Invoke(this);
    }

    /// <summary>会话工作目录镜像（装配期烧入工具闭包；切换会话时刷新）。null = 共享工作区。</summary>
    public string? WorkspaceDir { get; set; }

    /// <summary>装配该运行时时的配置代际（配置重载后 +1，旧代际运行时下次装配重建）。</summary>
    public int RuntimeVersion { get; set; } = -1;

    // ---- 流式缓冲（原实例级 _pendingDelta/_pendingReasoning，随运行归属会话） ----

    public StringBuilder PendingDelta { get; } = new();
    public DateTime LastFlushUtc { get; set; } = DateTime.MinValue;
    public StringBuilder PendingReasoning { get; } = new();
    public DateTime LastReasoningFlushUtc { get; set; } = DateTime.MinValue;

    public void ResetBuffers()
    {
        PendingDelta.Clear();
        PendingReasoning.Clear();
        LastFlushUtc = DateTime.MinValue;
        LastReasoningFlushUtc = DateTime.MinValue;
    }

    /// <summary>
    /// 该会话的权威轮次列表：运行启动前把当前可见 Turns 镜像进来；运行期间成员不变
    /// （编辑/发送/清空均被会话闸门挡住），步骤与文本写在 AgentTurn 对象上。
    /// 切换会话时可见集合被重建，落库以本列表为准；切回忙碌会话时据此恢复上屏。
    /// </summary>
    public List<AgentTurn> LiveTurns { get; set; } = new();

    /// <summary>该会话的权威计划镜像（运行期间 update_plan 由闭包写入这里，防止串到当前可见计划）。</summary>
    public List<PlanItemVM> PlanItems { get; set; } = new();
}
