using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.App.ViewModels;

public sealed class QueuedAgentMessage
{
    public QueuedAgentMessage(string text, bool isSteering, IReadOnlyList<AgentAttachment>? attachments = null)
    {
        Text = text;
        IsSteering = isSteering;
        Attachments = attachments ?? Array.Empty<AgentAttachment>();
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Text { get; }
    public bool IsSteering { get; }
    public IReadOnlyList<AgentAttachment> Attachments { get; }
    public bool Cancelled { get; set; }
    public string KindText => IsSteering ? "插队" : "排队";
    public string Preview
    {
        get
        {
            var prefix = Attachments.Count > 0 ? $"[附件{Attachments.Count}] " : string.Empty;
            var text = Text.Length <= 90 ? Text.Replace('\n', ' ') : Text[..90].Replace('\n', ' ') + "…";
            return prefix + text;
        }
    }
}

public partial class AgentViewModel
{
    private readonly HashSet<long> _queueRestoredSessions = new();
    private RelayCommand? _queueCommand;
    private RelayCommand? _steerCommand;
    private RelayCommand? _clearQueueCommand;
    private RelayCommand? _resumeQueueCommand;

    public ICommand QueueCommand => _queueCommand ??= new RelayCommand(
        _ => EnqueueInput(isSteering: false),
        _ => IsBusy && (!string.IsNullOrWhiteSpace(Input) || HasAttachments));

    public ICommand SteerCommand => _steerCommand ??= new RelayCommand(
        _ => EnqueueInput(isSteering: true),
        _ => IsBusy && (!string.IsNullOrWhiteSpace(Input) || HasAttachments));

    public ICommand ClearQueueCommand => _clearQueueCommand ??= new RelayCommand(
        _ => ClearQueue(),
        _ => HasQueuedMessages);

    public ICommand ResumeQueueCommand => _resumeQueueCommand ??= new RelayCommand(
        _ => _ = ResumeQueueAsync(),
        _ => CanResumeQueue);

    public ObservableCollection<QueuedAgentMessage> QueuedMessages => CurrentContext.PendingMessages;
    public bool HasQueuedMessages => QueuedMessages.Count > 0;
    public bool IsQueuePaused => CurrentContext.QueuePaused;
    public bool CanResumeQueue => HasQueuedMessages && !IsBusy;
    internal void RestoreQueueIfNeeded(SessionRunContext context)
    {
        if (context.SessionId <= 0 || !_queueRestoredSessions.Add(context.SessionId))
        {
            return;
        }

        foreach (var item in AgentQueueStore.Load(context.SessionId))
        {
            context.PendingMessages.Add(item);
        }

        if (context.PendingMessages.Count > 0)
        {
            context.QueuePaused = true;
            if (ReferenceEquals(context, CurrentContext))
            {
                ShowNotice("已恢复上次未发送的排队消息，可继续队列。");
            }
        }

        NotifyQueueState();
    }

    private void PersistQueue(SessionRunContext context)
        => AgentQueueStore.Save(context.SessionId, context.PendingMessages);

    public string QueueHeader => HasQueuedMessages
        ? $"待发送队列（{QueuedMessages.Count}）" + (IsQueuePaused ? " · 已暂停" : "")
        : string.Empty;

    private void EnqueueInput(bool isSteering)
    {
        var text = Input?.Trim();
        if (string.IsNullOrWhiteSpace(text) && !HasAttachments)
        {
            return;
        }

        if (!IsBusy)
        {
            _ = SendAsync();
            return;
        }

        var pending = Attachments.ToList();
        if (isSteering && pending.Count > 0)
        {
            isSteering = false;
            ShowNotice("附件消息不支持插队，已加入队列。");
        }

        var item = new QueuedAgentMessage(text ?? string.Empty, isSteering, pending);
        if (pending.Count > 0)
        {
            _attachmentManager.Clear();
        }
        CurrentContext.PendingMessages.Add(item);
        if (isSteering)
        {
            CurrentContext.SteeringQueue.Enqueue(item);
        }

        Input = string.Empty;
        PersistQueue(CurrentContext);
        NotifyQueueState();
        ShowNotice(isSteering
            ? "已插队：将在下一次模型/工具检查点插入当前轮。"
            : "已加入队列：当前轮结束后自动发送。");
    }

    public void RemoveQueuedMessage(QueuedAgentMessage item)
    {
        item.Cancelled = true;
        CurrentContext.PendingMessages.Remove(item);
        PersistQueue(CurrentContext);
        NotifyQueueState();
    }

    private void ClearQueue()
    {
        foreach (var item in CurrentContext.PendingMessages)
        {
            item.Cancelled = true;
        }

        CurrentContext.PendingMessages.Clear();
        while (CurrentContext.SteeringQueue.TryDequeue(out _))
        {
        }

        SetQueuePaused(CurrentContext, false);
        PersistQueue(CurrentContext);
        NotifyQueueState();
    }

    private async Task ResumeQueueAsync()
    {
        var context = CurrentContext;
        SetQueuePaused(context, false);
        await TryPumpQueueAsync(context);
    }

    private void SetQueuePaused(SessionRunContext context, bool paused)
    {
        if (context.QueuePaused == paused)
        {
            return;
        }

        context.QueuePaused = paused;
        _sidebar.RefreshStates();
        if (ReferenceEquals(context, CurrentContext))
        {
            NotifyQueueState();
        }
    }

    private void NotifyQueueState()
    {
        OnPropertyChanged(nameof(QueuedMessages));
        OnPropertyChanged(nameof(HasQueuedMessages));
        OnPropertyChanged(nameof(IsQueuePaused));
        OnPropertyChanged(nameof(CanResumeQueue));
        OnPropertyChanged(nameof(QueueHeader));
        RaiseQueueCommandStates();
        _sidebar.RefreshStates();
    }

    private void RaiseQueueCommandStates()
    {
        (QueueCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SteerCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearQueueCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ResumeQueueCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private IReadOnlyList<string> DrainSteering(SessionRunContext context)
    {
        var drained = new List<QueuedAgentMessage>();
        while (context.SteeringQueue.TryDequeue(out var item))
        {
            drained.Add(item);
        }

        if (drained.Count == 0)
        {
            return Array.Empty<string>();
        }

        var texts = drained.Where(i => !i.Cancelled).Select(i => i.Text).ToArray();
        _uiInvoke(() =>
        {
            foreach (var item in drained)
            {
                context.PendingMessages.Remove(item);
            }

            PersistQueue(context);
            if (ReferenceEquals(context, CurrentContext))
            {
                NotifyQueueState();
                if (texts.Length > 0)
                {
                    ShowNotice("已插入补充指令，将在下一步生效。");
                }
            }
        });
        return texts;
    }

    internal Func<AgentTurn, IReadOnlyList<AIContent>?, SessionRunContext, Task>? QueueTurnRunnerForTest { get; set; }

    internal async Task TryPumpQueueAsync(SessionRunContext context)
    {
        if (context.QueuePumpActive || context.IsBusy)
        {
            return;
        }

        if (context.PendingMessages.Count == 0)
        {
            return;
        }

        context.QueuePumpActive = true;
        try
        {
            while (!context.IsBusy && !context.QueuePaused)
            {
                var item = context.PendingMessages.FirstOrDefault();
                if (item is null)
                {
                    break;
                }

                while (context.SteeringQueue.TryDequeue(out _))
                {
                }

                context.PendingMessages.Remove(item);
                PersistQueue(context);
                NotifyQueueState();
                if (item.Cancelled)
                {
                    continue;
                }

                await RunQueuedMessageAsync(item, context);
            }
        }
        finally
        {
            context.QueuePumpActive = false;
            NotifyQueueState();
        }
    }
    private async Task RunQueuedMessageAsync(QueuedAgentMessage item, SessionRunContext context)
    {
        var displayText = item.Text;
        string? persistedText = null;
        var imagePaths = new List<string>();
        IReadOnlyList<AIContent>? contents = null;
        if (item.Attachments.Count > 0)
        {
            var build = await BuildAttachmentPrompt(displayText, item.Attachments,
                context.WorkspaceDir ?? CurrentWorkspaceRoot, context.SessionId);
            displayText = build.Display;
            persistedText = build.Persisted;
            imagePaths = build.ImagePaths;
            var list = new List<AIContent> { new TextContent(build.ModelText) };
            list.AddRange(build.Images);
            contents = list;
        }

        var turn = new AgentTurn(displayText, DateTime.UtcNow)
        {
            StorageText = persistedText,
            UiOrdinal = Turns.Count
        };
        foreach (var imagePath in imagePaths)
        {
            turn.Images.Add(imagePath);
        }

        if (ReferenceEquals(context, CurrentContext))
        {
            Turns.Add(turn);
        }

        context.LiveTurns.Add(turn);
        context.ActiveTurn = turn;
        if (QueueTurnRunnerForTest is not null)
        {
            await QueueTurnRunnerForTest(turn, contents, context);
        }
        else
        {
            await RunTurnAsync(turn, contents, context);
        }
    }
    public void BeginEditQueuedMessage(QueuedAgentMessage item)
    {
        var context = CurrentContext;
        if (!context.PendingMessages.Remove(item))
        {
            return;
        }

        item.Cancelled = true;
        if (!string.IsNullOrWhiteSpace(item.Text))
        {
            Input = item.Text;
        }

        var paths = item.Attachments.Select(a => a.Path).ToArray();
        if (paths.Length > 0)
        {
            _attachmentManager.AddFiles(paths);
        }

        PersistQueue(context);
        NotifyQueueState();
        ShowNotice("已把队列消息载入输入框，可修改后重新排队。");
    }

    public async Task PrioritizeQueuedMessageAsync(QueuedAgentMessage item)
    {
        var context = CurrentContext;
        if (!context.PendingMessages.Contains(item))
        {
            return;
        }

        context.PendingMessages.Remove(item);
        if (item.IsSteering)
        {
            item.Cancelled = true;
            var replacement = new QueuedAgentMessage(item.Text, isSteering: false, item.Attachments);
            context.PendingMessages.Insert(0, replacement);
        }
        else
        {
            context.PendingMessages.Insert(0, item);
        }

        SetQueuePaused(context, false);
        PersistQueue(context);
        NotifyQueueState();
        if (!IsBusy)
        {
            await TryPumpQueueAsync(context);
        }
    }
}
